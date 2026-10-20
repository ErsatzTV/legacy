using System.Diagnostics;
using System.Text;
using ErsatzTV.Infrastructure.Troubleshooting;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Troubleshooting;

// Tests that need the age CLI look in AGE_PATH, then PATH, and skip if it is missing
[TestFixture]
public class AgeEncryptorTests
{
    [SetUp]
    public void SetUp() => _tempDir = Directory.CreateTempSubdirectory("age-encryptor-tests").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDir, true);

    private const int ChunkSize = 64 * 1024;

    // from the age README
    private const string KnownRecipient = "age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8p";

    private static readonly int[] PayloadSizes = [0, 1, 65535, 65536, 65537, 131072, 1_000_000];

    private readonly AgeEncryptor _encryptor = new();
    private string _tempDir = string.Empty;

    [Test]
    public void Should_decrypt_with_age_cli(
        [ValueSource(nameof(PayloadSizes))]
        int size,
        [Values(int.MaxValue, 1, 1000)]
        int maxRead)
    {
        (string identity, string recipient) = NewIdentity();
        byte[] plaintext = RandomBytes(size);

        byte[] ciphertext = Encrypt(plaintext, maxRead, recipient);

        Decrypt(ciphertext, identity).ShouldBe(plaintext);
    }

    [Test]
    public void Should_decrypt_with_each_of_two_identities()
    {
        (string first, string firstRecipient) = NewIdentity();
        (string second, string secondRecipient) = NewIdentity();
        byte[] plaintext = RandomBytes(100_000);

        byte[] ciphertext = Encrypt(plaintext, int.MaxValue, firstRecipient, secondRecipient);

        Decrypt(ciphertext, first).ShouldBe(plaintext);
        Decrypt(ciphertext, second).ShouldBe(plaintext);
    }

    [Test]
    public void Should_not_decrypt_with_other_identity()
    {
        (_, string recipient) = NewIdentity();
        (string other, _) = NewIdentity();

        byte[] ciphertext = Encrypt(RandomBytes(10), int.MaxValue, recipient);

        TryDecrypt(ciphertext, other).ShouldBeNull();
    }

    [Test]
    public void Should_reject_tampered_header()
    {
        (string identity, string recipient) = NewIdentity();
        byte[] ciphertext = Encrypt(RandomBytes(1000), int.MaxValue, recipient);

        int bodyLine = Array.IndexOf(ciphertext, (byte)'\n', Array.IndexOf(ciphertext, (byte)'>')) + 1;
        ciphertext[bodyLine] = ciphertext[bodyLine] == (byte)'A' ? (byte)'B' : (byte)'A';

        TryDecrypt(ciphertext, identity).ShouldBeNull();
    }

    [Test]
    public void Should_reject_tampered_mac()
    {
        (string identity, string recipient) = NewIdentity();
        byte[] ciphertext = Encrypt(RandomBytes(1000), int.MaxValue, recipient);

        int mac = HeaderLength(ciphertext) - 2;
        ciphertext[mac] = ciphertext[mac] == (byte)'A' ? (byte)'B' : (byte)'A';

        TryDecrypt(ciphertext, identity).ShouldBeNull();
    }

    [Test]
    public void Should_reject_tampered_payload()
    {
        (string identity, string recipient) = NewIdentity();
        byte[] ciphertext = Encrypt(RandomBytes(1000), int.MaxValue, recipient);

        ciphertext[^20] ^= 1;

        TryDecrypt(ciphertext, identity).ShouldBeNull();
    }

    [Test]
    public void Should_reject_truncated_payload()
    {
        (string identity, string recipient) = NewIdentity();
        byte[] ciphertext = Encrypt(RandomBytes(1000), int.MaxValue, recipient);

        TryDecrypt(ciphertext[..^1], identity).ShouldBeNull();
    }

    [Test]
    public void Should_reject_dropped_final_chunk()
    {
        (string identity, string recipient) = NewIdentity();
        byte[] ciphertext = Encrypt(RandomBytes(ChunkSize * 2 + 100), int.MaxValue, recipient);

        // the new last chunk is not marked final
        TryDecrypt(ciphertext[..^(100 + 16)], identity).ShouldBeNull();
    }

    [Test]
    public void Should_reject_reordered_chunks()
    {
        (string identity, string recipient) = NewIdentity();
        byte[] ciphertext = Encrypt(RandomBytes(ChunkSize * 2 + 100), int.MaxValue, recipient);

        int payload = HeaderLength(ciphertext) + 16;
        const int Encrypted = ChunkSize + 16;
        byte[] first = ciphertext[payload..(payload + Encrypted)];
        byte[] second = ciphertext[(payload + Encrypted)..(payload + 2 * Encrypted)];
        second.CopyTo(ciphertext, payload);
        first.CopyTo(ciphertext, payload + Encrypted);

        TryDecrypt(ciphertext, identity).ShouldBeNull();
    }

    [Test]
    public void Should_reject_trailing_data()
    {
        (string identity, string recipient) = NewIdentity();
        byte[] ciphertext = Encrypt(RandomBytes(1000), int.MaxValue, recipient);

        TryDecrypt([..ciphertext, 0], identity).ShouldBeNull();
    }

    [Test]
    public void Should_use_fresh_randomness_for_each_encryption()
    {
        byte[] plaintext = RandomBytes(1000);

        byte[] first = EncryptUnchecked(plaintext, KnownRecipient);
        byte[] second = EncryptUnchecked(plaintext, KnownRecipient);

        string[] firstLines = HeaderLines(first);
        string[] secondLines = HeaderLines(second);

        firstLines[1].ShouldStartWith("-> X25519 ");
        firstLines[1].ShouldNotBe(secondLines[1]);
        firstLines[2].ShouldNotBe(secondLines[2]);
        firstLines[3].ShouldNotBe(secondLines[3]);

        int headerLength = HeaderLength(first);
        first[headerLength..(headerLength + 16)].ShouldNotBe(second[headerLength..(headerLength + 16)]);
        first[(headerLength + 16)..].ShouldNotBe(second[(headerLength + 16)..]);
    }

    [Test]
    public void Should_not_add_empty_chunk_after_exact_multiple(
        [Values(0, ChunkSize, ChunkSize * 2)]
        int size)
    {
        byte[] ciphertext = EncryptUnchecked(RandomBytes(size), KnownRecipient);

        int chunks = Math.Max(1, size / ChunkSize);
        (ciphertext.Length - HeaderLength(ciphertext) - 16).ShouldBe(size + chunks * 16);
    }

    [Test]
    public void Should_match_precomputed_length(
        [ValueSource(nameof(PayloadSizes))]
        int size,
        [Values(1, 2, 3)]
        int recipientCount)
    {
        string[] recipients = Enumerable.Repeat(KnownRecipient, recipientCount).ToArray();

        byte[] ciphertext = EncryptUnchecked(RandomBytes(size), recipients);

        ciphertext.LongLength.ShouldBe(_encryptor.GetCiphertextLength(size, recipientCount));
    }

    [Test]
    public void Should_write_age_header()
    {
        byte[] ciphertext = EncryptUnchecked(RandomBytes(10), KnownRecipient);

        Encoding.ASCII.GetString(ciphertext, 0, 32).ShouldBe("age-encryption.org/v1\n-> X25519 ");
    }

    [Test]
    public void Should_round_trip_bech32()
    {
        byte[] key = AgeEncryptor.ParseRecipient(KnownRecipient).GetEncoded();

        Bech32.Encode("age", key).ShouldBe(KnownRecipient);
    }

    [TestCase("AGE1QL3Z7HJY54PW3HYWW5AYYFG7ZQGVC7W3J2ELW8ZMRJ2KG5SFN9AQMCAC8P")]
    [TestCase("age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8q")]
    [TestCase("age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcac8")]
    [TestCase("age1ql3z7hjy54pw3hyww5ayyfg7zqgvc7w3j2elw8zmrj2kg5sfn9aqmcabc8p")]
    [TestCase("")]
    [TestCase("age1")]
    [TestCase("not a recipient")]
    public void Should_reject_invalid_recipient(string recipient) =>
        Should.Throw<FormatException>(() => EncryptUnchecked([], recipient));

    [Test]
    public void Should_reject_wrong_prefix()
    {
        string recipient = Bech32.Encode("agf", AgeEncryptor.ParseRecipient(KnownRecipient).GetEncoded());

        Should.Throw<FormatException>(() => EncryptUnchecked([], recipient));
    }

    [Test]
    public void Should_reject_wrong_key_length()
    {
        string recipient = Bech32.Encode("age", new byte[31]);

        Should.Throw<FormatException>(() => EncryptUnchecked([], recipient));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Should_reject_low_order_recipient(int u)
    {
        var key = new byte[32];
        key[0] = (byte)u;

        Should.Throw<FormatException>(() => EncryptUnchecked([], Bech32.Encode("age", key)));
    }

    [Test]
    public void Should_write_nothing_for_invalid_recipient()
    {
        using var output = new MemoryStream();

        Should.Throw<FormatException>(() =>
            _encryptor.Encrypt(new MemoryStream([1, 2, 3]), output, [KnownRecipient, "age1invalid"]));

        output.Length.ShouldBe(0);
    }

    [Test]
    public void Should_require_a_recipient() => Should.Throw<ArgumentException>(() => EncryptUnchecked([]));

    private byte[] Encrypt(byte[] plaintext, int maxRead, params string[] recipients)
    {
        using var input = new ShortReadStream(plaintext, maxRead);
        using var output = new MemoryStream();
        _encryptor.Encrypt(input, output, recipients);

        output.Length.ShouldBe(_encryptor.GetCiphertextLength(plaintext.Length, recipients.Length));
        return output.ToArray();
    }

    private byte[] EncryptUnchecked(byte[] plaintext, params string[] recipients)
    {
        using var output = new MemoryStream();
        _encryptor.Encrypt(new MemoryStream(plaintext), output, recipients);
        return output.ToArray();
    }

    private static int HeaderLength(byte[] ciphertext)
    {
        byte[] macLine = "\n--- "u8.ToArray();
        int mac = ciphertext.AsSpan().IndexOf(macLine);
        return Array.IndexOf(ciphertext, (byte)'\n', mac + 1) + 1;
    }

    private static string[] HeaderLines(byte[] ciphertext) =>
        Encoding.ASCII.GetString(ciphertext, 0, HeaderLength(ciphertext) - 1).Split('\n');

    private static byte[] RandomBytes(int size)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    private (string IdentityFile, string Recipient) NewIdentity()
    {
        string identity = Path.Combine(_tempDir, $"{Guid.NewGuid()}.key");
        RunTool("age-keygen", null, "-o", identity);
        string recipient = Encoding.ASCII.GetString(RunTool("age-keygen", null, "-y", identity).Stdout).Trim();
        return (identity, recipient);
    }

    private byte[] Decrypt(byte[] ciphertext, string identity) =>
        TryDecrypt(ciphertext, identity) ?? throw new AssertionException("age -d failed");

    private byte[]? TryDecrypt(byte[] ciphertext, string identity)
    {
        (int exitCode, byte[] stdout) = RunTool("age", ciphertext, "-d", "-i", identity);
        return exitCode == 0 ? stdout : null;
    }

    private static (int ExitCode, byte[] Stdout) RunTool(string tool, byte[]? stdin, params string[] args)
    {
        string? path = FindTool(tool);
        if (path is null)
        {
            // CI sets AGE_PATH, so fail there instead of skip
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGE_PATH")))
            {
                Assert.Fail($"{tool} not found in AGE_PATH");
            }

            Assert.Ignore($"{tool} not found; set AGE_PATH or put the age CLI on PATH");
        }

        var startInfo = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;

        // write stdin and read stdout at the same time, or a full pipe blocks
        var writer = Task.Run(() =>
        {
            if (stdin is not null)
            {
                process.StandardInput.BaseStream.Write(stdin);
            }

            process.StandardInput.Close();
        });

        using var stdout = new MemoryStream();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.BaseStream.CopyTo(stdout);
        process.WaitForExit();

        try
        {
            writer.Wait();
        }
        catch (AggregateException) when (process.ExitCode != 0)
        {
            // age can exit before it reads all input
        }

        _ = stderr.Result;
        return (process.ExitCode, stdout.ToArray());
    }

    private static string? FindTool(string tool)
    {
        string fileName = OperatingSystem.IsWindows() ? $"{tool}.exe" : tool;
        string? agePath = Environment.GetEnvironmentVariable("AGE_PATH");
        IEnumerable<string> dirs = string.IsNullOrWhiteSpace(agePath)
            ? (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            : [agePath];

        return dirs.Select(dir => Path.Combine(dir, fileName)).FirstOrDefault(File.Exists);
    }

    private sealed class ShortReadStream(byte[] data, int maxRead) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, maxRead));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using ChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace ErsatzTV.Infrastructure.Troubleshooting;

// BouncyCastle, not the BCL, for ChaCha20Poly1305: the BCL version is not supported on all platforms.
public sealed class AgeEncryptor : IAgeEncryptor
{
    private const string Version = "age-encryption.org/v1";
    private const string X25519Label = "age-encryption.org/v1/X25519";
    private const int FileKeySize = 16;
    private const int PayloadNonceSize = 16;
    private const int KeySize = 32;
    private const int TagSize = 16;
    private const int ChunkSize = 64 * 1024;

    // "-> X25519 <share>\n<body>\n"; share and body are 43 base64 chars each
    private const int StanzaLength = 10 + 43 + 1 + 43 + 1;

    // "age-encryption.org/v1\n" + "--- <mac>\n"
    private const int HeaderFixedLength = 22 + 48;

    public long GetCiphertextLength(long plaintextLength, int recipientCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(recipientCount, 1);

        long chunks = Math.Max(1, (plaintextLength + ChunkSize - 1) / ChunkSize);
        return HeaderFixedLength + (long)StanzaLength * recipientCount + PayloadNonceSize + plaintextLength +
               chunks * TagSize;
    }

    public void Encrypt(Stream plaintext, Stream output, IReadOnlyList<string> recipients)
    {
        if (recipients.Count == 0)
        {
            throw new ArgumentException("at least one recipient is required", nameof(recipients));
        }

        // fail before any output is written
        var publicKeys = recipients.Select(ParseRecipient).ToList();

        byte[] fileKey = RandomNumberGenerator.GetBytes(FileKeySize);
        byte[] payloadKey = [];
        try
        {
            output.Write(BuildHeader(fileKey, publicKeys));

            byte[] payloadNonce = RandomNumberGenerator.GetBytes(PayloadNonceSize);
            output.Write(payloadNonce);

            payloadKey = Hkdf(fileKey, payloadNonce, "payload", KeySize);
            WritePayload(payloadKey, plaintext, output);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
            CryptographicOperations.ZeroMemory(payloadKey);
        }
    }

    internal static X25519PublicKeyParameters ParseRecipient(string recipient)
    {
        byte[] key = Bech32.Decode(recipient, "age");
        if (key.Length != KeySize)
        {
            throw new FormatException($"X25519 recipient must be {KeySize} bytes, got {key.Length}");
        }

        return new X25519PublicKeyParameters(key);
    }

    private static byte[] BuildHeader(byte[] fileKey, List<X25519PublicKeyParameters> publicKeys)
    {
        var header = new StringBuilder();
        header.Append(Version).Append('\n');

        foreach (X25519PublicKeyParameters publicKey in publicKeys)
        {
            (byte[] share, byte[] body) = Wrap(fileKey, publicKey);
            header.Append("-> X25519 ").Append(Base64(share)).Append('\n');
            header.Append(Base64(body)).Append('\n');
        }

        // per spec, the MAC covers "---" but not the space after it
        header.Append("---");

        byte[] macKey = Hkdf(fileKey, [], "header", KeySize);
        try
        {
            byte[] mac = HMACSHA256.HashData(macKey, Encoding.ASCII.GetBytes(header.ToString()));
            header.Append(' ').Append(Base64(mac)).Append('\n');
            return Encoding.ASCII.GetBytes(header.ToString());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    private static (byte[] Share, byte[] Body) Wrap(byte[] fileKey, X25519PublicKeyParameters recipient)
    {
        byte[] ephemeralBytes = RandomNumberGenerator.GetBytes(KeySize);
        var sharedSecret = new byte[KeySize];
        byte[] wrapKey = [];
        try
        {
            var ephemeral = new X25519PrivateKeyParameters(ephemeralBytes);
            byte[] share = ephemeral.GeneratePublicKey().GetEncoded();

            var agreement = new X25519Agreement();
            agreement.Init(ephemeral);
            try
            {
                agreement.CalculateAgreement(recipient, sharedSecret, 0);
            }
            catch (InvalidOperationException ex)
            {
                throw new FormatException("X25519 recipient is a low-order point", ex);
            }

            // the spec requires this; BouncyCastle should already have thrown
            if (sharedSecret.All(b => b == 0))
            {
                throw new FormatException("X25519 recipient is a low-order point");
            }

            wrapKey = Hkdf(sharedSecret, [..share, ..recipient.GetEncoded()], X25519Label, KeySize);
            byte[] body = Seal(wrapKey, new byte[12], fileKey, fileKey.Length);
            return (share, body);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeralBytes);
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(wrapKey);
        }
    }

    private static void WritePayload(byte[] payloadKey, Stream plaintext, Stream output)
    {
        // read one extra byte to know if a full chunk is the last one; else a payload that is a multiple of
        // 64 KiB ends with an empty chunk, which age rejects
        var buffer = new byte[ChunkSize + 1];
        var nonce = new byte[12];
        var buffered = 0;
        long counter = 0;

        try
        {
            while (true)
            {
                buffered += plaintext.ReadAtLeast(
                    buffer.AsSpan(buffered),
                    buffer.Length - buffered,
                    false);

                bool isFinal = buffered <= ChunkSize;
                int length = Math.Min(buffered, ChunkSize);

                // 11-byte counter, then the last-chunk flag
                BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(3, 8), counter);
                nonce[11] = isFinal ? (byte)1 : (byte)0;

                output.Write(Seal(payloadKey, nonce, buffer, length));

                if (isFinal)
                {
                    return;
                }

                buffer[0] = buffer[ChunkSize];
                buffered = 1;
                counter++;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, int length)
    {
        var cipher = new ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce));

        var result = new byte[length + TagSize];
        int written = cipher.ProcessBytes(plaintext, 0, length, result, 0);
        cipher.DoFinal(result, written);
        return result;
    }

    private static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
    {
        var hkdf = new HkdfBytesGenerator(new Sha256Digest());
        hkdf.Init(new HkdfParameters(ikm, salt, Encoding.ASCII.GetBytes(info)));

        var result = new byte[length];
        hkdf.GenerateBytes(result, 0, length);
        return result;
    }

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');
}

namespace ErsatzTV.Core.Interfaces.Troubleshooting;

public interface IAgeEncryptor
{
    void Encrypt(Stream plaintext, Stream output, IReadOnlyList<string> recipients);

    // Lets callers check size limits before they encrypt.
    long GetCiphertextLength(long plaintextLength, int recipientCount);
}

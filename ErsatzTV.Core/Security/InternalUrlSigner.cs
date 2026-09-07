using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ErsatzTV.Core.Security;

public static class InternalUrlSigner
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static string Sign(DateTimeOffset expires, params string[] parts)
    {
        string canonical = string.Join('\0', parts) + '\0' + expires.ToUnixTimeSeconds();
        byte[] bytes = Encoding.UTF8.GetBytes(canonical);
        byte[] hash = HMACSHA256.HashData(Key, bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool Verify(string exp, string sig, params string[] parts)
    {
        if (!long.TryParse(exp, CultureInfo.InvariantCulture, out long num))
        {
            return false;
        }

        try
        {
            DateTimeOffset expires = DateTimeOffset.FromUnixTimeSeconds(num);
            string expected = Sign(expires, parts);
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            byte[] actualBytes = Encoding.UTF8.GetBytes(sig);
            return DateTimeOffset.Now < expires && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

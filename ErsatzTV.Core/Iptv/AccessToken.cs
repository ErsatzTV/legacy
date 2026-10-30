using System.Text.RegularExpressions;

namespace ErsatzTV.Core.Iptv;

public static partial class AccessToken
{
    public static string Sanitize(string token) =>
        !string.IsNullOrEmpty(token) && JwtAlphabet().IsMatch(token) ? token : null;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+\z")]
    private static partial Regex JwtAlphabet();
}

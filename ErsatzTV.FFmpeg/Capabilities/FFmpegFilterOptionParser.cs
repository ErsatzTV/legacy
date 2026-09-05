using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace ErsatzTV.FFmpeg.Capabilities;

public static partial class FFmpegFilterOptionParser
{
    public static IReadOnlySet<string> Parse(string filterHelpOutput) =>
        filterHelpOutput.Split("\n").Map(s => s.Trim())
            .Bind(l => ParseLine(l))
            .ToImmutableHashSet();

    private static Option<string> ParseLine(string input)
    {
        Match match = FilterOptionRegex().Match(input);
        return match.Success ? match.Groups[1].Value : Option<string>.None;
    }

    [GeneratedRegex(@"^([\w-]+)\s+<")]
    private static partial Regex FilterOptionRegex();
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public partial class NextLogger
{
    [GeneratedRegex(
        @"^\[\S+ (?<level>TRACE|DEBUG|INFO|WARN|ERROR) (?<target>[^\]]+)\] (?<msg>.*)$",
        RegexOptions.Singleline)]
    private static partial Regex NextLogLine();

    public static void LogNextLine(string line, ILogger logger)
    {
        Match match = NextLogLine().Match(line);
        if (!match.Success)
        {
            logger.LogDebug("{Line:l}", line);
            return;
        }

        LogLevel level = match.Groups["level"].Value switch
        {
            "ERROR" => LogLevel.Error,
            "WARN" => LogLevel.Warning,
            "INFO" => LogLevel.Information,
            "TRACE" => LogLevel.Trace,
            _ => LogLevel.Debug
        };

        logger.Log(level, "[{Target:l}] {Line:l}", match.Groups["target"].Value, match.Groups["msg"].Value);
    }
}

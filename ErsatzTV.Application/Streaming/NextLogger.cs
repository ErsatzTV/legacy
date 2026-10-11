using System.Text.Json;
using CliWrap;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public static class NextLogger
{
    public static Command WithJsonLogs(this Command command) =>
        command.WithEnvironmentVariables(env => env.Set("ETV_LOG_FORMAT", "json"));

    public static void LogNextLine(string line, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        foreach (NextLogLine parsed in Parse(line))
        {
            switch (parsed.Channel, parsed.IsFfmpeg)
            {
                case (not null, true):
                    logger.Log(
                        parsed.Level,
                        "[next::channel {Channel:l}::ffmpeg] {Line:l}",
                        parsed.Channel,
                        parsed.Message);
                    break;
                case (not null, false):
                    logger.Log(parsed.Level, "[next::channel {Channel:l}] {Line:l}", parsed.Channel, parsed.Message);
                    break;
                case (null, true):
                    logger.Log(parsed.Level, "[next::ffmpeg] {Line:l}", parsed.Message);
                    break;
                default:
                    logger.Log(parsed.Level, "[next] {Line:l}", parsed.Message);
                    break;
            }

            return;
        }

        // panics and argument errors bypass the logger
        logger.LogWarning("[next] {Line:l}", line);
    }

    public static Option<NextLogLine> Parse(string line)
    {
        if (!line.StartsWith('{'))
        {
            return None;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("message", out JsonElement message) ||
                !root.TryGetProperty("level", out JsonElement level))
            {
                return None;
            }

            string channel = null;
            if (root.TryGetProperty("span", out JsonElement span) &&
                span.TryGetProperty("number", out JsonElement number))
            {
                channel = number.ValueKind is JsonValueKind.String ? number.GetString() : number.GetRawText();
            }

            LogLevel logLevel = level.GetString() switch
            {
                "ERROR" => LogLevel.Error,
                "WARN" => LogLevel.Warning,
                "INFO" => LogLevel.Information,
                "TRACE" => LogLevel.Trace,
                _ => LogLevel.Debug
            };

            bool isFfmpeg = root.TryGetProperty("target", out JsonElement target) &&
                            target.ValueKind is JsonValueKind.String && target.GetString() == "ffmpeg";

            return new NextLogLine(logLevel, channel, isFfmpeg, message.GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            return None;
        }
    }
}

public record NextLogLine(LogLevel Level, string Channel, bool IsFfmpeg, string Message);

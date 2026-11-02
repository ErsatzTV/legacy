using System.Globalization;
using ErsatzTV.Core.Next.Description;
using ErsatzTV.Infrastructure.Scheduling;

namespace ErsatzTV.Application.Streaming;

public static class NextContract
{
    public const long DescribeVersion = 1;

    public const string PlaylistFileName = "live.m3u8";
    public const string SubtitlePlaylistFileName = "live_sub.m3u8";
    public const string ReadyFileName = ".ready";
    public const string HeartbeatFileName = ".heartbeat";

    public static List<string> Check(Describe describe) =>
        Check(describe, ChannelConfigConverter.ChannelConfigVersion, PlayoutItemConverter.PlayoutVersion);

    public static List<string> Check(Describe describe, string channelConfigVersion, string playoutVersion)
    {
        // a bump means fields changed, so the checks below are unreliable
        if (describe.DescribeVersion != DescribeVersion)
        {
            return [$"describe version {describe.DescribeVersion} is not supported (expected {DescribeVersion})"];
        }

        var problems = new List<string>();

        CheckSchema(problems, "channel config", channelConfigVersion, describe.Contract.ChannelConfigSchema);
        CheckSchema(problems, "playout", playoutVersion, describe.Contract.PlayoutSchema);

        OutputFiles files = describe.Contract.OutputFiles;
        CheckFile(problems, "playlist", PlaylistFileName, files.Playlist);
        CheckFile(problems, "subtitle playlist", SubtitlePlaylistFileName, files.SubtitlePlaylist);
        CheckFile(problems, "ready", ReadyFileName, files.Ready);
        CheckFile(problems, "heartbeat", HeartbeatFileName, files.Heartbeat);

        return problems;
    }

    private static void CheckSchema(
        List<string> problems,
        string name,
        string versionUri,
        SchemaInfo supported)
    {
        if (!TryParseVersion(versionUri, out long breaking, out long compatible))
        {
            problems.Add($"unable to parse {name} schema version {versionUri}");
            return;
        }

        if (breaking != supported.Breaking || compatible > supported.Compatible)
        {
            problems.Add($"{name} schema {versionUri} is not accepted; ersatztv-channel accepts up to {supported.Uri}");
        }
    }

    private static void CheckFile(List<string> problems, string name, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            problems.Add($"{name} file is {actual}, expected {expected}");
        }
    }

    // 0.B.C: B must match, C must not exceed the binary's
    private static bool TryParseVersion(string versionUri, out long breaking, out long compatible)
    {
        breaking = 0;
        compatible = 0;

        string[] parts = versionUri.Split('/').Last().Split('.');
        return parts.Length == 3
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out breaking)
               && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out compatible);
    }
}

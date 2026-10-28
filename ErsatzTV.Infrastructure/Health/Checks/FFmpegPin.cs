using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ErsatzTV.Infrastructure.Health.Checks;

// the ErsatzTV-ffmpeg build in .github/ffmpeg.json, embedded at build time
public sealed partial record FFmpegPin(string Tag, string Version, string DevelopSha, int LastRevision)
{
    public static readonly Lazy<FFmpegPin> Bundled = new(LoadBundled);

    public bool IsDevelop => DevelopSha is not null;

    public string ReleaseUrl => IsDevelop
        ? $"https://github.com/ErsatzTV/ffmpeg-develop-builds/releases/tag/{Tag}"
        : $"https://github.com/ErsatzTV/ErsatzTV-ffmpeg/releases/tag/{Tag}";

    public static FFmpegPin Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        string tag = doc.RootElement.GetProperty("tag").GetString() ?? string.Empty;

        Match release = ReleaseTag().Match(tag);
        if (release.Success)
        {
            int revision = int.Parse(release.Groups[2].Value, CultureInfo.InvariantCulture);
            return new FFmpegPin(tag, release.Groups[1].Value, null, revision);
        }

        Match develop = DevelopTag().Match(tag);
        if (develop.Success && doc.RootElement.TryGetProperty("last_release", out JsonElement lastRelease))
        {
            Match last = ReleaseTag().Match(lastRelease.GetString() ?? string.Empty);
            if (last.Success && last.Groups[1].Value == develop.Groups[1].Value)
            {
                return new FFmpegPin(
                    tag,
                    develop.Groups[1].Value,
                    develop.Groups[2].Value,
                    int.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture));
            }
        }

        throw new InvalidOperationException($"Unrecognized ffmpeg pin '{tag}'");
    }

    private static FFmpegPin LoadBundled()
    {
        using Stream stream = typeof(FFmpegPin).Assembly.GetManifestResourceStream("ffmpeg.json")
                              ?? throw new InvalidOperationException("Missing embedded ffmpeg.json");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    [GeneratedRegex(@"^(\d+\.\d+\.\d+)-(\d+)$")]
    private static partial Regex ReleaseTag();

    [GeneratedRegex(@"^(\d+\.\d+\.\d+)-([0-9a-f]{8})-develop$")]
    private static partial Regex DevelopTag();
}

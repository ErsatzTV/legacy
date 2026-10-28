using System.Globalization;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Health;
using ErsatzTV.Core.Health.Checks;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.FFmpeg.Capabilities;

namespace ErsatzTV.Infrastructure.Health.Checks;

public class FFmpegVersionHealthCheck(
    IConfigElementRepository configElementRepository,
    IHardwareCapabilitiesFactory hardwareCapabilitiesFactory)
    : BaseHealthCheck, IFFmpegVersionHealthCheck
{
    // the oldest release that passes, set by hand: Proxmox VE installs update ffmpeg separately from ErsatzTV
    private const int MinimumRevision = 5;

    public override string Title => "FFmpeg Version";

    public async Task<HealthCheckResult> Check(CancellationToken cancellationToken)
    {
        FFmpegPin pin = FFmpegPin.Bundled.Value;
        var link = new HealthCheckLink(pin.ReleaseUrl);

        Option<ConfigElement> maybeFFmpegPath =
            await configElementRepository.GetConfigElement(ConfigElementKey.FFmpegPath, cancellationToken);
        if (maybeFFmpegPath.IsNone)
        {
            return FailResult("Unable to locate ffmpeg", "Unable to locate ffmpeg", link);
        }

        Option<ConfigElement> maybeFFprobePath =
            await configElementRepository.GetConfigElement(ConfigElementKey.FFprobePath, cancellationToken);
        if (maybeFFprobePath.IsNone)
        {
            return FailResult("Unable to locate ffprobe", "Unable to locate ffprobe", link);
        }

        foreach (ConfigElement ffmpegPath in maybeFFmpegPath)
        {
            Option<string> maybeVersion =
                await hardwareCapabilitiesFactory.GetFFmpegVersion(ffmpegPath.Value, cancellationToken);
            if (maybeVersion.IsNone || maybeVersion.Exists(string.IsNullOrWhiteSpace))
            {
                return WarningResult("Unable to determine ffmpeg version", "Unable to determine ffmpeg version", link);
            }

            foreach (string version in maybeVersion)
            {
                foreach (HealthCheckResult result in ValidateVersion(pin, version, "ffmpeg", link))
                {
                    return result;
                }
            }
        }

        foreach (ConfigElement ffprobePath in maybeFFprobePath)
        {
            Option<string> maybeVersion =
                await hardwareCapabilitiesFactory.GetFFmpegVersion(ffprobePath.Value, cancellationToken);
            if (maybeVersion.IsNone || maybeVersion.Exists(string.IsNullOrWhiteSpace))
            {
                return WarningResult(
                    "Unable to determine ffprobe version",
                    "Unable to determine ffprobe version",
                    link);
            }

            foreach (string version in maybeVersion)
            {
                foreach (HealthCheckResult result in ValidateVersion(pin, version, "ffprobe", link))
                {
                    return result;
                }
            }
        }

        return new HealthCheckResult("FFmpeg Version", HealthCheckStatus.Pass, string.Empty, string.Empty, None);
    }

    private Option<HealthCheckResult> ValidateVersion(FFmpegPin pin, string version, string app, HealthCheckLink link)
    {
        switch (Classify(pin, version))
        {
            case FFmpegVersionStatus.TooOld:
                return FailResult(
                    $"{app} version {version} is too old; please install ErsatzTV-ffmpeg {pin.Tag}!",
                    $"{app} version is too old",
                    link);
            case FFmpegVersionStatus.NotCurrent:
                return WarningResult(
                    $"{app} version {version} is not a current ErsatzTV-ffmpeg build; please install ErsatzTV-ffmpeg {pin.Tag}!",
                    $"{app} version is not current",
                    link);
            case FFmpegVersionStatus.OtherDevelop:
                return WarningResult(
                    $"{app} version {version} is a develop build of ErsatzTV-ffmpeg that this ErsatzTV doesn't ship; please install ErsatzTV-ffmpeg {pin.Tag}!",
                    $"{app} version is not current",
                    link);
            default:
                return None;
        }
    }

    internal static FFmpegVersionStatus Classify(FFmpegPin pin, string version)
    {
        if (version.StartsWith("3.", StringComparison.OrdinalIgnoreCase) ||
            version.StartsWith("4.", StringComparison.OrdinalIgnoreCase) ||
            version.StartsWith("5.", StringComparison.OrdinalIgnoreCase) ||
            version.StartsWith("6.", StringComparison.OrdinalIgnoreCase) ||
            version.StartsWith("7.", StringComparison.OrdinalIgnoreCase))
        {
            return FFmpegVersionStatus.TooOld;
        }

        string v = version.StartsWith('n') ? version[1..] : version;
        var prefix = $"{pin.Version}-etv.";
        if (!v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return FFmpegVersionStatus.NotCurrent;
        }

        string build = v[prefix.Length..];
        if (build.StartsWith("dev.", StringComparison.OrdinalIgnoreCase))
        {
            return pin.IsDevelop && string.Equals(build[4..], pin.DevelopSha, StringComparison.OrdinalIgnoreCase)
                ? FFmpegVersionStatus.Current
                : FFmpegVersionStatus.OtherDevelop;
        }

        // newer revisions pass: they can reach /releases/latest (e.g. Proxmox) before a legacy release bumps the pin.
        // With a develop pin, the release that includes it is newer than the one it was built on.
        int minimum = pin.IsDevelop ? Math.Max(MinimumRevision, pin.LastRevision + 1) : MinimumRevision;
        return int.TryParse(build, NumberStyles.None, CultureInfo.InvariantCulture, out int revision) && revision >= minimum
            ? FFmpegVersionStatus.Current
            : FFmpegVersionStatus.NotCurrent;
    }
}

public enum FFmpegVersionStatus
{
    Current,
    TooOld,
    NotCurrent,
    OtherDevelop
}

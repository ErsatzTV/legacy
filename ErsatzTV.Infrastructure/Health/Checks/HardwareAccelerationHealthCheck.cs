using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Health;
using ErsatzTV.Core.Health.Checks;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Next;
using ErsatzTV.Core.Next.Description;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Health.Checks;

public record HardwareAccelerationProblems(
    List<string> Software,
    List<string> SoftwareEncode,
    List<string> NotConfigured,
    List<HardwareAccelerationKind> Available);

public class HardwareAccelerationHealthCheck(
    IDbContextFactory<TvContext> dbContextFactory,
    INextProbeService nextProbeService)
    : BaseHealthCheck, IHardwareAccelerationHealthCheck
{
    private static readonly HealthCheckLink HardwareLink = new("system/troubleshooting?tab=hardware");

    public override string Title => "Hardware Acceleration";

    public async Task<HealthCheckResult> Check(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // the ffmpeg version check reports a missing ffmpeg
        Option<string> maybeFFmpegPath =
            await dbContext.ConfigElements.GetValue<string>(ConfigElementKey.FFmpegPath, cancellationToken);
        if (maybeFFmpegPath.IsNone)
        {
            return NotApplicableResult();
        }

        List<Channel> channels = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.FFmpegProfile)
            .ToListAsync(cancellationToken);

        Option<NextProbeResult> maybeProbe = nextProbeService.Latest;
        if (maybeProbe.IsNone)
        {
            // at startup this joins the probe that is already running
            foreach (string ffmpegPath in maybeFFmpegPath)
            {
                maybeProbe = await nextProbeService.Refresh(ffmpegPath, cancellationToken);
            }
        }

        foreach (NextProbeResult probe in maybeProbe)
        {
            return ToResult(Evaluate(channels, probe.Capability));
        }

        return WarningResult(
            "Unable to probe hardware with ErsatzTV Next, so hardware acceleration can't be checked",
            "Unable to probe hardware",
            HardwareLink);
    }

    public static HardwareAccelerationProblems Evaluate(List<Channel> channels, RuntimeCapability capability)
    {
        var software = new List<string>();
        var softwareEncode = new List<string>();

        var available = Enum.GetValues<HardwareAccelerationKind>()
            .Filter(k => NextHardware.ToNextAccel(k) is { } accel && NextHardware.AnyAvailable(capability, accel))
            .ToList();

        var profiles = channels
            .Map(c => c.FFmpegProfile)
            .Filter(p => p.HardwareAcceleration is not HardwareAccelerationKind.None)
            .DistinctBy(p => p.Id)
            .OrderBy(p => p.Name);

        foreach (FFmpegProfile profile in profiles)
        {
            NextHardwareTarget target = NextHardware.ToTarget(
                profile.HardwareAcceleration,
                profile.VaapiDevice,
                profile.VaapiDriver,
                profile.VideoFormat,
                profile.BitDepth);
            NextHardwareCheck check = NextHardware.Check(target, capability);

            string accel = profile.HardwareAcceleration.ToString();
            string error = check.Entry.Bind(e => Optional(e.Error)).Map(e => $" ({e})").IfNone(string.Empty);

            switch (check.Status)
            {
                case NextHardwareStatus.Unavailable:
                    software.Add($"{profile.Name}: {accel} isn't available{error}");
                    break;
                case NextHardwareStatus.UnsupportedAccel:
                    software.Add($"{profile.Name}: the next engine doesn't support {accel}");
                    break;
                case NextHardwareStatus.NotOnPlatform:
                    software.Add($"{profile.Name}: the next engine can't use {accel} on this platform");
                    break;
                case NextHardwareStatus.DeviceNotProbed:
                    software.Add(
                        target.VaapiDevice.StartsWith("/dev/dri/card", StringComparison.Ordinal)
                            ? $"{profile.Name}: {target.VaapiDevice} isn't a render node"
                            : $"{profile.Name}: {target.VaapiDevice} wasn't found");
                    break;
                case NextHardwareStatus.SoftwareEncode:
                    softwareEncode.Add(
                        $"{profile.Name}: {accel} can't encode {NextHardware.ToProbeName(target.Format)} {target.BitDepth}-bit");
                    break;
            }
        }

        var notConfigured = channels
            .Filter(c => c.FFmpegProfile.HardwareAcceleration is HardwareAccelerationKind.None)
            .OrderBy(c => c.SortNumber)
            .Map(c => $"{c.Number} - {c.Name}")
            .ToList();

        return new HardwareAccelerationProblems(software, softwareEncode, notConfigured, available);
    }

    private HealthCheckResult ToResult(HardwareAccelerationProblems problems)
    {
        var messages = new List<string>();
        if (problems.Software.Count > 0)
        {
            messages.Add(
                $"Channels using these FFmpeg profiles will stream in software: {string.Join("; ", problems.Software)}.");
        }

        if (problems.SoftwareEncode.Count > 0)
        {
            messages.Add(
                $"Channels using these FFmpeg profiles will encode in software: {string.Join("; ", problems.SoftwareEncode)}.");
        }

        bool suggest = problems.NotConfigured.Count > 0 && problems.Available.Count > 0;
        if (suggest)
        {
            messages.Add(
                $"These channels don't use hardware acceleration (available here: {string.Join(", ", problems.Available)}): {string.Join(", ", problems.NotConfigured)}.");
        }

        string message = string.Join(" ", messages);

        if (problems.Software.Count > 0)
        {
            return FailResult(
                message,
                $"{Count(problems.Software.Count, "FFmpeg profile")} will stream in software",
                HardwareLink);
        }

        if (problems.SoftwareEncode.Count > 0)
        {
            return WarningResult(
                message,
                $"{Count(problems.SoftwareEncode.Count, "FFmpeg profile")} will encode in software",
                HardwareLink);
        }

        if (suggest)
        {
            return WarningResult(
                message,
                $"{Count(problems.NotConfigured.Count, "channel")} not configured for hardware acceleration",
                HardwareLink);
        }

        if (problems.Available.Count == 0)
        {
            return InfoResult(
                "No hardware acceleration is available to the next engine on this machine",
                "No hardware acceleration available");
        }

        return OkResult();
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

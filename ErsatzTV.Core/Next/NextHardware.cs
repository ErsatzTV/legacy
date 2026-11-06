using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Next.Config;
using ErsatzTV.Core.Next.Description;

namespace ErsatzTV.Core.Next;

public record NextHardwareTarget(
    bool HardwareRequested,
    AccelEnum? Accel,
    string VaapiDevice,
    VaapiDriverEnum? VaapiDriver,
    VideoFormat Format,
    long BitDepth);

public enum NextHardwareStatus
{
    Software,
    NotProbed,

    // legacy has the accel, next doesn't
    UnsupportedAccel,
    NotOnPlatform,
    DeviceNotProbed,

    // probe failed or timed out, or ffmpeg lacks the hwaccel
    Unavailable,

    // hardware still decodes and filters
    SoftwareEncode,
    Hardware
}

// Working: other available entries for the accel that hardware-encode this format and bit depth
public record NextHardwareCheck(NextHardwareStatus Status, Option<AccelRuntime> Entry, List<AccelRuntime> Working);

public static class NextHardware
{
    public const string DefaultVaapiDevice = "/dev/dri/renderD128";

    public static AccelEnum? ToNextAccel(HardwareAccelerationKind kind) =>
        kind switch
        {
            HardwareAccelerationKind.Amf => AccelEnum.Amf,
            HardwareAccelerationKind.Nvenc => AccelEnum.Cuda,
            HardwareAccelerationKind.Qsv => AccelEnum.Qsv,
            HardwareAccelerationKind.Rkmpp => AccelEnum.Rkmpp,
            HardwareAccelerationKind.Vaapi => AccelEnum.Vaapi,
            HardwareAccelerationKind.VideoToolbox => AccelEnum.Videotoolbox,
            _ => null
        };

    public static VaapiDriverEnum? ToNextVaapiDriver(VaapiDriver driver) =>
        driver switch
        {
            VaapiDriver.iHD => VaapiDriverEnum.Ihd,
            VaapiDriver.i965 => VaapiDriverEnum.I965,
            VaapiDriver.RadeonSI => VaapiDriverEnum.Radeonsi,
            _ => null
        };

    public static VideoFormat ToNextVideoFormat(FFmpegProfileVideoFormat format) =>
        format switch
        {
            FFmpegProfileVideoFormat.Hevc => VideoFormat.Hevc,
            FFmpegProfileVideoFormat.Mpeg2Video => VideoFormat.Mpeg2Video,
            _ => VideoFormat.H264
        };

    public static long ToNextBitDepth(FFmpegProfileVideoFormat format, FFmpegProfileBitDepth bitDepth) =>
        (format, bitDepth) switch
        {
            (FFmpegProfileVideoFormat.Mpeg2Video, _) => 8,
            (_, FFmpegProfileBitDepth.TenBit) => 10,
            _ => 8
        };

    public static NextHardwareTarget ToTarget(
        HardwareAccelerationKind kind,
        string vaapiDevice,
        VaapiDriver vaapiDriver,
        FFmpegProfileVideoFormat format,
        FFmpegProfileBitDepth bitDepth) =>
        new(
            kind is not HardwareAccelerationKind.None,
            ToNextAccel(kind),
            vaapiDevice,
            ToNextVaapiDriver(vaapiDriver),
            ToNextVideoFormat(format),
            ToNextBitDepth(format, bitDepth));

    public static string ToProbeName(AccelEnum accel) =>
        accel switch
        {
            AccelEnum.Amf => "amf",
            AccelEnum.Cuda => "cuda",
            AccelEnum.Qsv => "qsv",
            AccelEnum.Rkmpp => "rkmpp",
            AccelEnum.Vaapi => "vaapi",
            AccelEnum.Videotoolbox => "videotoolbox",
            AccelEnum.Vulkan => "vulkan",
            _ => throw new ArgumentOutOfRangeException(nameof(accel), accel, null)
        };

    public static string ToProbeName(VaapiDriverEnum driver) =>
        driver switch
        {
            VaapiDriverEnum.Ihd => "ihd",
            VaapiDriverEnum.I965 => "i965",
            VaapiDriverEnum.Radeonsi => "radeonsi",
            _ => throw new ArgumentOutOfRangeException(nameof(driver), driver, null)
        };

    public static string ToProbeName(VideoFormat format) =>
        format switch
        {
            VideoFormat.H264 => "h264",
            VideoFormat.Hevc => "hevc",
            VideoFormat.Mpeg2Video => "mpeg2video",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };

    public static bool AnyAvailable(RuntimeCapability capability, AccelEnum accel)
    {
        string accelName = ToProbeName(accel);
        return capability.Accels.Any(a => a.Accel == accelName && a.Status == AccelStatus.Available);
    }

    public static List<string> VaapiDevices(RuntimeCapability capability)
    {
        string accelName = ToProbeName(AccelEnum.Vaapi);
        return capability.Accels
            .Filter(a => a.Accel == accelName && a.Device is not null)
            .Map(a => a.Device)
            .Distinct()
            .OrderBy(d => d)
            .ToList();
    }

    public static NextHardwareCheck Check(NextHardwareTarget target, Option<RuntimeCapability> maybeCapability)
    {
        var none = new NextHardwareCheck(NextHardwareStatus.Software, Option<AccelRuntime>.None, []);

        if (!target.HardwareRequested)
        {
            return none;
        }

        if (target.Accel is not { } accel)
        {
            return none with { Status = NextHardwareStatus.UnsupportedAccel };
        }

        foreach (RuntimeCapability capability in maybeCapability)
        {
            string accelName = ToProbeName(accel);
            var sameAccel = capability.Accels.Filter(a => a.Accel == accelName).ToList();
            if (sameAccel.Count == 0)
            {
                return none with { Status = NextHardwareStatus.NotOnPlatform };
            }

            Option<AccelRuntime> maybeEntry = accel is AccelEnum.Vaapi
                ? sameAccel.Find(a => a.Device == ProbeVaapiDevice(target) && a.Driver == ProbeVaapiDriver(target))
                : sameAccel.HeadOrNone();

            List<AccelRuntime> working = sameAccel
                .Filter(a => maybeEntry.ForAll(e => !ReferenceEquals(a, e)))
                .Filter(a => a.Status == AccelStatus.Available && EncodesInHardware(a, target))
                .ToList();

            foreach (AccelRuntime entry in maybeEntry)
            {
                NextHardwareStatus status = entry.Status switch
                {
                    AccelStatus.Available when EncodesInHardware(entry, target) => NextHardwareStatus.Hardware,
                    AccelStatus.Available => NextHardwareStatus.SoftwareEncode,
                    _ => NextHardwareStatus.Unavailable
                };

                return new NextHardwareCheck(status, entry, working);
            }

            return new NextHardwareCheck(NextHardwareStatus.DeviceNotProbed, Option<AccelRuntime>.None, working);
        }

        return none with { Status = NextHardwareStatus.NotProbed };
    }

    private static string ProbeVaapiDevice(NextHardwareTarget target) =>
        string.IsNullOrWhiteSpace(target.VaapiDevice) ? DefaultVaapiDevice : target.VaapiDevice;

    private static string ProbeVaapiDriver(NextHardwareTarget target) =>
        target.VaapiDriver is { } driver ? ToProbeName(driver) : null;

    private static bool EncodesInHardware(AccelRuntime entry, NextHardwareTarget target)
    {
        string format = ToProbeName(target.Format);
        return entry.Encode.Any(e => e.Format == format && e.BitDepths.Contains(target.BitDepth));
    }
}

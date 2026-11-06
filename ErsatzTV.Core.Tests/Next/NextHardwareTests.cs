using System.Text.Json;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Next;
using ErsatzTV.Core.Next.Config;
using ErsatzTV.Core.Next.Description;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Next;

[TestFixture]
public class NextHardwareTests
{
    // real output from an RX 9060 XT (next v0.2.5-92e93d72-develop); ffmpeg path changed
    private const string RadeonProbe =
        """
        {
          "ffmpeg": { "path": "/usr/local/bin/ffmpeg", "version": "n8.1.2-etv.dev.4089c130", "hwaccels": ["amf", "cuda", "opencl", "qsv", "vaapi", "vulkan"] },
          "accels": [
            { "accel": "amf", "configured": false, "status": "probe_failed", "error": "failed to probe AMF capabilities: error detecting amf capabilities: failed to load the AMF runtime: dlopen failed", "device": null, "driver": null, "decode": [], "encode": [] },
            { "accel": "cuda", "configured": false, "status": "probe_failed", "error": "failed to probe NVIDIA capabilities: error detecting nvidia capabilities: cuInit failed", "device": null, "driver": null, "decode": [], "encode": [] },
            { "accel": "qsv", "configured": false, "status": "probe_failed", "error": "failed to probe QSV capabilities: error detecting qsv capabilities: no hardware implementation found", "device": null, "driver": null, "decode": [], "encode": [] },
            { "accel": "vaapi", "configured": false, "status": "available", "error": null, "device": "/dev/dri/renderD128", "driver": null,
              "decode": [{ "format": "av1", "pixel_formats": ["yuv420p", "yuv420p10le"] }, { "format": "h264", "pixel_formats": ["yuv420p"] }, { "format": "hevc", "pixel_formats": ["yuv420p", "yuv420p10le"] }, { "format": "vp9", "pixel_formats": ["yuv420p", "yuv420p10le"] }],
              "encode": [{ "format": "h264", "bit_depths": [8] }, { "format": "hevc", "bit_depths": [8, 10] }] },
            { "accel": "vaapi", "configured": false, "status": "probe_failed", "error": "failed to probe VAAPI capabilities: error detecting vaapi capabilities: vaInitialize failed on /dev/dri/renderD128 with driver iHD: invalid parameter (18)", "device": "/dev/dri/renderD128", "driver": "ihd", "decode": [], "encode": [] },
            { "accel": "vaapi", "configured": false, "status": "probe_failed", "error": "failed to probe VAAPI capabilities: error detecting vaapi capabilities: vaInitialize failed on /dev/dri/renderD128 with driver i965: unknown libva error (-1)", "device": "/dev/dri/renderD128", "driver": "i965", "decode": [], "encode": [] },
            { "accel": "vaapi", "configured": false, "status": "available", "error": null, "device": "/dev/dri/renderD128", "driver": "radeonsi",
              "decode": [{ "format": "av1", "pixel_formats": ["yuv420p", "yuv420p10le"] }, { "format": "h264", "pixel_formats": ["yuv420p"] }, { "format": "hevc", "pixel_formats": ["yuv420p", "yuv420p10le"] }, { "format": "vp9", "pixel_formats": ["yuv420p", "yuv420p10le"] }],
              "encode": [{ "format": "h264", "bit_depths": [8] }, { "format": "hevc", "bit_depths": [8, 10] }] },
            { "accel": "vulkan", "configured": false, "status": "available", "error": null, "device": null, "driver": null,
              "decode": [{ "format": "av1", "pixel_formats": ["yuv420p", "yuv420p10le"] }, { "format": "h264", "pixel_formats": ["yuv420p"] }, { "format": "hevc", "pixel_formats": ["yuv420p", "yuv420p10le"] }],
              "encode": [{ "format": "h264", "bit_depths": [8] }, { "format": "hevc", "bit_depths": [8, 10] }] }
          ]
        }
        """;

    private const string SoftwareOnlyProbe =
        """
        {
          "ffmpeg": { "path": "ffmpeg", "version": "n8.1.2", "hwaccels": [] },
          "accels": [
            { "accel": "qsv", "configured": false, "status": "ffmpeg_missing", "error": "ffmpeg has no `qsv` hwaccel", "device": null, "driver": null, "decode": [], "encode": [] },
            { "accel": "vaapi", "configured": false, "status": "ffmpeg_missing", "error": "ffmpeg has no `vaapi` hwaccel", "device": "/dev/dri/renderD128", "driver": null, "decode": [], "encode": [] }
          ]
        }
        """;

    private const string HungVaapiProbe =
        """
        {
          "ffmpeg": { "path": "ffmpeg", "version": "n8.1.2", "hwaccels": ["vaapi"] },
          "accels": [
            { "accel": "vaapi", "configured": false, "status": "timeout", "error": "probe did not finish within 10 s", "device": "/dev/dri/renderD128", "driver": null, "decode": [], "encode": [] },
            { "accel": "vaapi", "configured": false, "status": "skipped", "error": null, "device": "/dev/dri/renderD128", "driver": "ihd", "decode": [], "encode": [] }
          ]
        }
        """;

    private static Option<RuntimeCapability> Parse(string runtimeCapability) =>
        Optional(Describe.FromJson($$"""{ "describe_version": 1, "runtime_capability": {{runtimeCapability}} }""")
            .RuntimeCapability);

    private static NextHardwareTarget Target(
        HardwareAccelerationKind kind,
        string device = "/dev/dri/renderD128",
        VaapiDriver driver = VaapiDriver.Default,
        FFmpegProfileVideoFormat format = FFmpegProfileVideoFormat.H264,
        FFmpegProfileBitDepth bitDepth = FFmpegProfileBitDepth.EightBit) =>
        NextHardware.ToTarget(kind, device, driver, format, bitDepth);

    private static NextHardwareCheck Check(NextHardwareTarget target, string probe = RadeonProbe) =>
        NextHardware.Check(target, Parse(probe));

    private static List<string> Drivers(NextHardwareCheck check) =>
        check.Working.Map(a => a.Driver ?? "default").ToList();

    [Test]
    public void Software_Profile_Is_Not_Checked()
    {
        Check(Target(HardwareAccelerationKind.None)).Status.ShouldBe(NextHardwareStatus.Software);
    }

    [Test]
    public void Accel_Next_Lacks_Is_Unsupported()
    {
        Check(Target(HardwareAccelerationKind.V4l2m2m)).Status.ShouldBe(NextHardwareStatus.UnsupportedAccel);
    }

    [Test]
    public void No_Probe_Is_Not_Probed()
    {
        NextHardware.Check(Target(HardwareAccelerationKind.Vaapi), Option<RuntimeCapability>.None)
            .Status.ShouldBe(NextHardwareStatus.NotProbed);
    }

    [Test]
    public void Accel_Missing_From_Probe_Is_Not_On_Platform()
    {
        Check(Target(HardwareAccelerationKind.VideoToolbox)).Status.ShouldBe(NextHardwareStatus.NotOnPlatform);
    }

    [Test]
    public void Vaapi_Default_Driver_Encodes_In_Hardware()
    {
        NextHardwareCheck check = Check(Target(HardwareAccelerationKind.Vaapi));

        check.Status.ShouldBe(NextHardwareStatus.Hardware);
        check.Entry.Map(e => e.Driver is null).IfNone(false).ShouldBeTrue();
        Drivers(check).ShouldBe(["radeonsi"]);
    }

    [Test]
    public void Vaapi_Empty_Device_Is_Default_Render_Node()
    {
        Check(Target(HardwareAccelerationKind.Vaapi, device: "")).Status.ShouldBe(NextHardwareStatus.Hardware);
    }

    [Test]
    public void Vaapi_Hevc_Ten_Bit_Encodes_In_Hardware()
    {
        Check(
                Target(
                    HardwareAccelerationKind.Vaapi,
                    driver: VaapiDriver.RadeonSI,
                    format: FFmpegProfileVideoFormat.Hevc,
                    bitDepth: FFmpegProfileBitDepth.TenBit))
            .Status.ShouldBe(NextHardwareStatus.Hardware);
    }

    [Test]
    public void Vaapi_Wrong_Driver_Is_Unavailable_With_Working_Alternatives()
    {
        NextHardwareCheck check = Check(Target(HardwareAccelerationKind.Vaapi, driver: VaapiDriver.iHD));

        check.Status.ShouldBe(NextHardwareStatus.Unavailable);
        check.Entry.Map(e => e.Error).IfNone(string.Empty).ShouldContain("with driver iHD");
        Drivers(check).ShouldBe(["default", "radeonsi"]);
    }

    [Test]
    public void Vaapi_Format_Without_Hardware_Encode_Is_Software_Encode()
    {
        NextHardwareCheck check = Check(
            Target(HardwareAccelerationKind.Vaapi, format: FFmpegProfileVideoFormat.Mpeg2Video));

        check.Status.ShouldBe(NextHardwareStatus.SoftwareEncode);
        check.Working.ShouldBeEmpty();
    }

    [Test]
    public void Vaapi_Bit_Depth_Without_Hardware_Encode_Is_Software_Encode()
    {
        Check(Target(HardwareAccelerationKind.Vaapi, bitDepth: FFmpegProfileBitDepth.TenBit))
            .Status.ShouldBe(NextHardwareStatus.SoftwareEncode);
    }

    [Test]
    public void Vaapi_Unprobed_Device_Lists_Working_Entries()
    {
        NextHardwareCheck check = Check(Target(HardwareAccelerationKind.Vaapi, device: "/dev/dri/card1"));

        check.Status.ShouldBe(NextHardwareStatus.DeviceNotProbed);
        check.Entry.IsNone.ShouldBeTrue();
        Drivers(check).ShouldBe(["default", "radeonsi"]);
    }

    [Test]
    public void Failed_Accel_Is_Unavailable()
    {
        NextHardwareCheck check = Check(Target(HardwareAccelerationKind.Qsv));

        check.Status.ShouldBe(NextHardwareStatus.Unavailable);
        check.Entry.Map(e => e.Error).IfNone(string.Empty).ShouldContain("no hardware implementation found");
    }

    [Test]
    public void Ffmpeg_Without_Hwaccel_Is_Unavailable()
    {
        Check(Target(HardwareAccelerationKind.Vaapi), SoftwareOnlyProbe)
            .Status.ShouldBe(NextHardwareStatus.Unavailable);
    }

    [Test]
    public void Timed_Out_And_Skipped_Vaapi_Are_Unavailable()
    {
        Check(Target(HardwareAccelerationKind.Vaapi), HungVaapiProbe).Status.ShouldBe(NextHardwareStatus.Unavailable);
        Check(Target(HardwareAccelerationKind.Vaapi, driver: VaapiDriver.iHD), HungVaapiProbe)
            .Status.ShouldBe(NextHardwareStatus.Unavailable);
    }

    [Test]
    public void Any_Available_Looks_At_Every_Entry_For_The_Accel()
    {
        RuntimeCapability probe = Parse(RadeonProbe).IfNone(() => throw new InvalidOperationException());

        NextHardware.AnyAvailable(probe, AccelEnum.Vaapi).ShouldBeTrue();
        NextHardware.AnyAvailable(probe, AccelEnum.Qsv).ShouldBeFalse();
        NextHardware.AnyAvailable(probe, AccelEnum.Videotoolbox).ShouldBeFalse();
    }

    [Test]
    public void Vaapi_Devices_Are_Distinct()
    {
        RuntimeCapability probe = Parse(RadeonProbe).IfNone(() => throw new InvalidOperationException());

        NextHardware.VaapiDevices(probe).ShouldBe(["/dev/dri/renderD128"]);
    }

    // the probe uses channel config names
    [Test]
    public void Probe_Names_Match_Channel_Config_Json()
    {
        foreach (AccelEnum accel in Enum.GetValues<AccelEnum>())
        {
            Serialized(new Video { Accel = accel }, "accel").ShouldBe(NextHardware.ToProbeName(accel));
        }

        foreach (VaapiDriverEnum driver in Enum.GetValues<VaapiDriverEnum>())
        {
            Serialized(new Video { VaapiDriver = driver }, "vaapi_driver").ShouldBe(NextHardware.ToProbeName(driver));
        }

        foreach (VideoFormat format in Enum.GetValues<VideoFormat>())
        {
            Serialized(new Video { Format = format }, "format").ShouldBe(NextHardware.ToProbeName(format));
        }
    }

    private static string Serialized(Video video, string property)
    {
        var config = new ChannelConfig { Normalization = new Normalization { Video = video } };
        using JsonDocument document = JsonDocument.Parse(config.ToJson());
        return document.RootElement.GetProperty("normalization").GetProperty("video").GetProperty(property)
            .GetString();
    }
}

using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Next.Description;
using ErsatzTV.Infrastructure.Health.Checks;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Infrastructure.Tests.Health;

[TestFixture]
public class HardwareAccelerationHealthCheckTests
{
    private static readonly RuntimeCapability Probe = Describe.FromJson(
        """
        {
          "describe_version": 1,
          "runtime_capability": {
            "ffmpeg": { "path": "ffmpeg", "version": "n8.1.2", "hwaccels": ["qsv", "vaapi"] },
            "accels": [
              { "accel": "qsv", "configured": false, "status": "probe_failed", "error": "no hardware implementation found", "device": null, "driver": null, "decode": [], "encode": [] },
              { "accel": "vaapi", "configured": false, "status": "available", "error": null, "device": "/dev/dri/renderD128", "driver": null,
                "decode": [{ "format": "h264", "pixel_formats": ["yuv420p"] }],
                "encode": [{ "format": "h264", "bit_depths": [8] }, { "format": "hevc", "bit_depths": [8, 10] }] }
            ]
          }
        }
        """).RuntimeCapability!;

    private static int _nextId;

    private static Channel Channel(FFmpegProfile profile) =>
        new(Guid.NewGuid())
        {
            Number = profile.Name,
            SortNumber = ++_nextId,
            Name = profile.Name,
            FFmpegProfile = profile,
            FFmpegProfileId = profile.Id
        };

    private static HardwareAccelerationProblems Evaluate(params FFmpegProfile[] profiles) =>
        HardwareAccelerationHealthCheck.Evaluate(profiles.Map(Channel).ToList(), Probe);

    private static FFmpegProfile Profile(
        string name,
        HardwareAccelerationKind kind,
        FFmpegProfileVideoFormat format = FFmpegProfileVideoFormat.H264,
        FFmpegProfileBitDepth bitDepth = FFmpegProfileBitDepth.EightBit,
        string device = "/dev/dri/renderD128") =>
        new()
        {
            Id = ++_nextId,
            Name = name,
            HardwareAcceleration = kind,
            VideoFormat = format,
            BitDepth = bitDepth,
            VaapiDevice = device,
            VaapiDriver = VaapiDriver.Default
        };

    [Test]
    public void Working_Profiles_Have_No_Problems()
    {
        HardwareAccelerationProblems problems = Evaluate(
            Profile("h264", HardwareAccelerationKind.Vaapi),
            Profile("hevc10", HardwareAccelerationKind.Vaapi, FFmpegProfileVideoFormat.Hevc, FFmpegProfileBitDepth.TenBit));

        problems.Software.ShouldBeEmpty();
        problems.SoftwareEncode.ShouldBeEmpty();
    }

    [Test]
    public void Unavailable_Accel_Streams_In_Software()
    {
        HardwareAccelerationProblems problems = Evaluate(Profile("qsv", HardwareAccelerationKind.Qsv));

        problems.Software.ShouldBe(["qsv: Qsv isn't available (no hardware implementation found)"]);
    }

    [Test]
    public void Card_Node_Streams_In_Software()
    {
        HardwareAccelerationProblems problems = Evaluate(
            Profile("card", HardwareAccelerationKind.Vaapi, device: "/dev/dri/card1"));

        problems.Software.ShouldBe(["card: /dev/dri/card1 isn't a render node"]);
    }

    [Test]
    public void Accel_Missing_From_Probe_Streams_In_Software()
    {
        HardwareAccelerationProblems problems = Evaluate(Profile("nvenc", HardwareAccelerationKind.Nvenc));

        problems.Software.ShouldBe(["nvenc: the next engine can't use Nvenc on this platform"]);
    }

    [Test]
    public void Software_Channels_Are_Listed_With_Available_Accels()
    {
        HardwareAccelerationProblems problems = Evaluate(
            Profile("software", HardwareAccelerationKind.None),
            Profile("vaapi", HardwareAccelerationKind.Vaapi));

        problems.NotConfigured.ShouldBe(["software - software"]);
        problems.Available.ShouldBe([HardwareAccelerationKind.Vaapi]);
    }

    [Test]
    public void Shared_Profile_Is_Listed_Once()
    {
        FFmpegProfile qsv = Profile("qsv", HardwareAccelerationKind.Qsv);

        Evaluate(qsv, qsv).Software.Count.ShouldBe(1);
    }

    [Test]
    public void Format_Without_Hardware_Encode_Encodes_In_Software()
    {
        HardwareAccelerationProblems problems = Evaluate(
            Profile("mpeg2", HardwareAccelerationKind.Vaapi, FFmpegProfileVideoFormat.Mpeg2Video));

        problems.Software.ShouldBeEmpty();
        problems.SoftwareEncode.ShouldBe(["mpeg2: Vaapi can't encode mpeg2video 8-bit"]);
    }
}

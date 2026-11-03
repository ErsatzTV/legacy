using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Next.Description;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Streaming;

[TestFixture]
public class NextContractTests
{
    private const string ChannelConfigVersion = "https://ersatztv.org/channel/version/0.1.3";
    private const string PlayoutVersion = "https://ersatztv.org/playout/version/0.0.7";

    private const string DescribeJson =
        """
        {
          "describe_version": 1,
          "binary": { "version": "0.2.4+local", "commit": null },
          "contract": {
            "playout_schema": { "uri": "https://ersatztv.org/playout/version/0.0.7", "breaking": 0, "compatible": 7 },
            "channel_config_schema": { "uri": "https://ersatztv.org/channel/version/0.1.3", "breaking": 1, "compatible": 3 },
            "output_files": {
              "playlist": "live.m3u8",
              "subtitle_playlist": "live_sub.m3u8",
              "segment_template": "live%06d.ts",
              "ready": ".ready",
              "heartbeat": ".heartbeat"
            },
            "segment_seconds": 4,
            "keyframe_seconds": 2
          },
          "schema_capability": {
            "video_formats": ["h264", "hevc", "mpeg2video"],
            "audio_formats": ["aac", "ac3"],
            "audio_copy_formats": ["aac", "ac3", "eac3", "mp2", "mp3"],
            "accels": ["amf", "cuda", "qsv", "rkmpp", "vaapi", "videotoolbox", "vulkan"],
            "subtitle_modes": ["burn", "convert"]
          },
          "runtime_capability": null
        }
        """;

    private static Describe Parse() => Describe.FromJson(DescribeJson);

    [Test]
    public void Current_Legacy_Versions_Match_Current_Binary()
    {
        NextContract.Check(Parse()).ShouldBeEmpty();
    }

    [Test]
    public void Older_Compatible_Version_Is_Accepted()
    {
        NextContract.Check(Parse(), "https://ersatztv.org/channel/version/0.1.0", PlayoutVersion).ShouldBeEmpty();
    }

    [Test]
    public void Newer_Compatible_Version_Is_Rejected()
    {
        List<string> problems = NextContract.Check(
            Parse(),
            "https://ersatztv.org/channel/version/0.1.4",
            PlayoutVersion);

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("channel config");
    }

    [Test]
    public void Different_Breaking_Version_Is_Rejected()
    {
        List<string> problems = NextContract.Check(
            Parse(),
            ChannelConfigVersion,
            "https://ersatztv.org/playout/version/0.1.0");

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("playout");
    }

    [Test]
    public void Unparseable_Version_Is_Rejected()
    {
        NextContract.Check(Parse(), "https://ersatztv.org/channel/version/latest", PlayoutVersion).Count.ShouldBe(1);
    }

    [Test]
    public void Renamed_Output_File_Is_Rejected()
    {
        Describe describe = Parse();
        describe.Contract.OutputFiles.Ready = "ready";

        List<string> problems = NextContract.Check(describe, ChannelConfigVersion, PlayoutVersion);

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("ready");
    }

    [Test]
    public void Unknown_Describe_Version_Is_Rejected()
    {
        Describe describe = Parse();
        describe.DescribeVersion = 2;

        NextContract.Check(describe, ChannelConfigVersion, PlayoutVersion).Count.ShouldBe(1);
    }
}

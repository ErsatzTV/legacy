using System.Diagnostics;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Next.Description;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Streaming;

[TestFixture]
public class NextContractTests
{
    // made-up versions: these tests cover the rules, not legacy's current versions
    private const string ChannelBase = "https://ersatztv.org/channel/version/";
    private const string PlayoutBase = "https://ersatztv.org/playout/version/";
    private const long ChannelBreaking = 4;
    private const long ChannelCompatible = 6;
    private const long PlayoutBreaking = 2;
    private const long PlayoutCompatible = 9;

    private static readonly string ChannelVersion = $"{ChannelBase}0.{ChannelBreaking}.{ChannelCompatible}";
    private static readonly string PlayoutVersion = $"{PlayoutBase}0.{PlayoutBreaking}.{PlayoutCompatible}";

    private static readonly string DescribeJson =
        $$"""
        {
          "describe_version": {{NextContract.DescribeVersion}},
          "binary": { "version": "0.2.4+local", "commit": null },
          "contract": {
            "playout_schema": { "uri": "{{PlayoutVersion}}", "breaking": {{PlayoutBreaking}}, "compatible": {{PlayoutCompatible}} },
            "channel_config_schema": { "uri": "{{ChannelVersion}}", "breaking": {{ChannelBreaking}}, "compatible": {{ChannelCompatible}} },
            "output_files": {
              "playlist": "{{NextContract.PlaylistFileName}}",
              "subtitle_playlist": "{{NextContract.SubtitlePlaylistFileName}}",
              "segment_template": "live%06d.ts",
              "ready": "{{NextContract.ReadyFileName}}",
              "heartbeat": "{{NextContract.HeartbeatFileName}}"
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
    public void Same_Versions_Are_Accepted()
    {
        NextContract.Check(Parse(), ChannelVersion, PlayoutVersion).ShouldBeEmpty();
    }

    [Test]
    public void Older_Compatible_Version_Is_Accepted()
    {
        NextContract.Check(Parse(), $"{ChannelBase}0.{ChannelBreaking}.0", PlayoutVersion).ShouldBeEmpty();
    }

    [Test]
    public void Newer_Compatible_Version_Is_Rejected()
    {
        List<string> problems = NextContract.Check(
            Parse(),
            $"{ChannelBase}0.{ChannelBreaking}.{ChannelCompatible + 1}",
            PlayoutVersion);

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("channel config");
    }

    [Test]
    public void Different_Breaking_Version_Is_Rejected()
    {
        List<string> problems = NextContract.Check(
            Parse(),
            ChannelVersion,
            $"{PlayoutBase}0.{PlayoutBreaking + 1}.0");

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("playout");
    }

    [Test]
    public void Unparseable_Version_Is_Rejected()
    {
        NextContract.Check(Parse(), $"{ChannelBase}latest", PlayoutVersion).Count.ShouldBe(1);
    }

    [Test]
    public void Renamed_Output_File_Is_Rejected()
    {
        Describe describe = Parse();
        describe.Contract.OutputFiles.Ready = "ready";

        List<string> problems = NextContract.Check(describe, ChannelVersion, PlayoutVersion);

        problems.Count.ShouldBe(1);
        problems[0].ShouldContain("ready");
    }

    [Test]
    public void Unknown_Describe_Version_Is_Rejected()
    {
        Describe describe = Parse();
        describe.DescribeVersion = NextContract.DescribeVersion + 1;

        NextContract.Check(describe, ChannelVersion, PlayoutVersion).Count.ShouldBe(1);
    }

    // CI downloads the pinned build into ETV_TEST_NEXT_FOLDER
    [Test]
    public async Task Current_Versions_Match_Pinned_Binary()
    {
        string folder = Environment.GetEnvironmentVariable("ETV_TEST_NEXT_FOLDER");
        if (string.IsNullOrWhiteSpace(folder))
        {
            Assert.Ignore("set ETV_TEST_NEXT_FOLDER to a folder with ersatztv-channel");
        }

        string binary = Path.Combine(folder, OperatingSystem.IsWindows() ? "ersatztv-channel.exe" : "ersatztv-channel");
        using var process = Process.Start(
            new ProcessStartInfo(binary, "describe") { RedirectStandardOutput = true, UseShellExecute = false });
        process.ShouldNotBeNull();

        string json = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        process.ExitCode.ShouldBe(0);

        NextContract.Check(Describe.FromJson(json)).ShouldBeEmpty();
    }
}

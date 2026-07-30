using System;
using System.Collections.Generic;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.FFmpeg.InputOption;
using ErsatzTV.FFmpeg.OutputFormat;
using ErsatzTV.FFmpeg.Pipeline;
using ErsatzTV.FFmpeg.Preset;
using ErsatzTV.FFmpeg.State;
using LanguageExt;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using static LanguageExt.Prelude;

namespace ErsatzTV.FFmpeg.Tests.Pipeline;

[TestFixture]
public class QsvPipelineBuilderTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Test]
    public void Scale_And_Pad_Fold_Into_Single_Hardware_Vpp_Qsv()
    {
        string command = BuildPadCommand(vppQsvSupportsPad: true);

        // scale and pad fold into a single vpp_qsv instance that pads on the GPU
        command.ShouldContain("vpp_qsv=w=1440:h=1080:pad_w=1920:pad_h=1080:pad_x=-1:pad_y=-1:pad_color=black");

        // exactly one vpp_qsv instance (two chained instances drop the final frame at EOF)
        command.Split("vpp_qsv").Length.ShouldBe(2);

        // no software pad
        command.ShouldNotContain("pad=1920:1080");
    }

    [Test]
    public void Frames_Stay_In_Hardware_Through_Setpts_Into_The_Qsv_Encoder()
    {
        string command = BuildPadCommand(vppQsvSupportsPad: true);

        // setpts/fps run on the hardware frame and hevc_qsv ingests it directly: no download anywhere
        command.ShouldNotContain("hwdownload");
        command.ShouldContain("pad_color=black,setsar=1,setpts=PTS-STARTPTS,fps=24");
        command.ShouldContain("hevc_qsv");
    }

    [Test]
    public void Download_Is_Retained_When_A_Colorspace_Conversion_Follows()
    {
        string command = BuildPadCommand(vppQsvSupportsPad: true, colorsAreBt709: true);

        // a downstream software colorspace filter still needs system memory, so the download stays
        // exactly where it is today: right after the pad, before setpts, and byte-identical to the
        // unfolded path (format=nv12, not a bare hwdownload)
        command.ShouldContain("pad_color=black,setsar=1,hwdownload,format=nv12,setpts=PTS-STARTPTS");
        command.ShouldContain("colorspace=");
    }

    [Test]
    public void Pad_Falls_Back_To_Software_When_Pad_Option_Unavailable()
    {
        string command = BuildPadCommand(vppQsvSupportsPad: false);

        // software fallback: download to system memory, pad, then continue
        command.ShouldNotContain("vpp_qsv=pad_w");
        command.ShouldContain("hwdownload,format=nv12,pad=1920:1080:-1:-1:color=black");
    }

    private string BuildPadCommand(bool vppQsvSupportsPad, bool colorsAreBt709 = false)
    {
        var videoInputFile = new VideoInputFile(
            "/tmp/whatever.mkv",
            new List<VideoStream>
            {
                new(
                    0,
                    VideoFormat.H264,
                    VideoProfile.Main,
                    new PixelFormatYuv420P(),
                    ColorParams.Default,
                    new FrameSize(640, 480),
                    "1:1",
                    "4:3",
                    FrameRate.DefaultFrameRate,
                    false,
                    ScanKind.Progressive)
            });

        var desiredState = new FrameState(
            true,
            false,
            VideoFormat.Hevc,
            VideoProfile.Main,
            VideoPreset.Unset,
            false,
            new PixelFormatNv12("yuv420p"),
            new FrameSize(1440, 1080),
            new FrameSize(1920, 1080),
            Option<FrameSize>.None,
            FFmpegFilterMode.HardwareIfPossible,
            false,
            Option<FrameRate>.None,
            2000,
            4000,
            90_000,
            colorsAreBt709,
            false);

        var ffmpegState = new FFmpegState(
            false,
            HardwareAccelerationMode.Qsv,
            HardwareAccelerationMode.Qsv,
            Option<string>.None,
            Option<string>.None,
            TimeSpan.FromSeconds(1),
            Option<TimeSpan>.None,
            false,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            OutputFormatKind.MpegTs,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            TimeSpan.Zero,
            Option<int>.None,
            Option<int>.None,
            false,
            false,
            "clip",
            false);

        var hardwareCapabilities = Substitute.For<IHardwareCapabilities>();
        hardwareCapabilities.CanDecode(
                Arg.Any<string>(),
                Arg.Any<Option<string>>(),
                Arg.Any<Option<IPixelFormat>>(),
                Arg.Any<ColorParams>())
            .Returns(FFmpegCapability.Hardware);
        hardwareCapabilities.CanEncode(
                Arg.Any<string>(),
                Arg.Any<Option<string>>(),
                Arg.Any<Option<IPixelFormat>>())
            .Returns(FFmpegCapability.Hardware);
        hardwareCapabilities.GetRateControlMode(Arg.Any<string>(), Arg.Any<Option<IPixelFormat>>())
            .Returns(Option<RateControlMode>.None);

        var vppQsvOptions = new System.Collections.Generic.HashSet<string> { "w", "h", "format", "deinterlace" };
        if (vppQsvSupportsPad)
        {
            vppQsvOptions.UnionWith(["pad_w", "pad_h", "pad_x", "pad_y", "pad_color"]);
        }

        var ffmpegCapabilities = new FFmpegCapabilities(
            string.Empty,
            new System.Collections.Generic.HashSet<string> { FFmpegKnownHardwareAcceleration.Qsv.Name },
            new System.Collections.Generic.HashSet<string>(),
            new System.Collections.Generic.HashSet<string>(),
            new System.Collections.Generic.HashSet<string>(),
            new System.Collections.Generic.HashSet<string>(),
            new System.Collections.Generic.HashSet<string>(),
            new System.Collections.Generic.Dictionary<string, IReadOnlySet<string>>
            {
                [FFmpegKnownFilter.VppQsv.Name] = vppQsvOptions
            });

        var builder = new QsvPipelineBuilder(
            ffmpegCapabilities,
            hardwareCapabilities,
            HardwareAccelerationMode.Qsv,
            videoInputFile,
            Option<AudioInputFile>.None,
            Option<WatermarkInputFile>.None,
            Option<SubtitleInputFile>.None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);

        FFmpegPipeline result = builder.Build(ffmpegState, desiredState);

        IList<string> arguments = CommandGenerator.GenerateArguments(
            videoInputFile,
            Option<AudioInputFile>.None,
            Option<WatermarkInputFile>.None,
            Option<ConcatInputFile>.None,
            Option<GraphicsEngineInput>.None,
            result.PipelineSteps,
            result.IsIntelVaapiOrQsv);

        return string.Join(" ", arguments);
    }
}

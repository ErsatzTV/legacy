using System;
using System.Collections.Generic;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Encoder.Nvenc;
using ErsatzTV.FFmpeg.Format;
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

namespace ErsatzTV.FFmpeg.Tests;

[TestFixture]
public class RotationTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    // phone video: stored as 1920x1080 with a 90 degree display matrix; ffmpeg auto-rotates to 1080x1920
    private static VideoInputFile RotatedInput(int rotation) =>
        new(
            "/tmp/portrait.mp4",
            new List<VideoStream>
            {
                new(
                    0,
                    VideoFormat.H264,
                    VideoProfile.High,
                    new PixelFormatYuv420P(),
                    ColorParams.Default,
                    rotation is 90 or 270 ? new FrameSize(1080, 1920) : new FrameSize(1920, 1080),
                    "1:1",
                    rotation is 90 or 270 ? "1080:1920" : "16:9",
                    FrameRate.DefaultFrameRate,
                    false,
                    ScanKind.Progressive) { Rotation = rotation }
            });

    private static AudioInputFile Audio() =>
        new(
            "/tmp/portrait.mp4",
            new List<AudioStream> { new(1, AudioFormat.Aac, 2) },
            new AudioState(AudioFormat.Ac3, 2, 192, 384, 48, false, AudioFilter.None, Option<double>.None));

    private static FrameState DesiredState(VideoStream videoStream)
    {
        var resolution = new FrameSize(1920, 1080);
        return new FrameState(
            true,
            false,
            VideoFormat.H264,
            VideoProfile.High,
            VideoPreset.Unset,
            false,
            new PixelFormatYuv420P(),
            videoStream.SquarePixelFrameSize(resolution),
            resolution,
            Option<FrameSize>.None,
            FFmpegFilterMode.HardwareIfPossible,
            false,
            Option<FrameRate>.None,
            2000,
            4000,
            90_000,
            false,
            false);
    }

    private static FFmpegState FFmpegState(HardwareAccelerationMode mode) =>
        new(
            false,
            mode,
            mode,
            Option<string>.None,
            Option<string>.None,
            TimeSpan.Zero,
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

    private static IHardwareCapabilities NvidiaCapabilities()
    {
        IHardwareCapabilities caps = Substitute.For<IHardwareCapabilities>();
        caps.CanDecode(
                Arg.Any<string>(),
                Arg.Any<Option<string>>(),
                Arg.Any<Option<IPixelFormat>>(),
                Arg.Any<ColorParams>())
            .Returns(FFmpegCapability.Hardware);
        caps.CanEncode(Arg.Any<string>(), Arg.Any<Option<string>>(), Arg.Any<Option<IPixelFormat>>())
            .Returns(FFmpegCapability.Hardware);
        caps.GetRateControlMode(Arg.Any<string>(), Arg.Any<Option<IPixelFormat>>())
            .Returns(Option<RateControlMode>.None);
        return caps;
    }

    private static string Command(VideoInputFile video, AudioInputFile audio, FFmpegPipeline pipeline)
    {
        IList<string> arguments = CommandGenerator.GenerateArguments(
            video,
            audio,
            None,
            None,
            None,
            pipeline.PipelineSteps,
            pipeline.IsIntelVaapiOrQsv);

        var command = string.Join(" ", arguments);
        Console.WriteLine($"Generated command: ffmpeg {command}");
        return command;
    }

    [Test]
    public void Software_Rotated_Video_Should_Scale_And_Pad_As_Portrait()
    {
        VideoInputFile video = RotatedInput(90);
        AudioInputFile audio = Audio();
        FrameState desiredState = DesiredState(video.VideoStreams[0]);

        // 1080x1920 fit into 1920x1080 => 607x1080 (rounded to even by the scale filter)
        desiredState.ScaledSize.Height.ShouldBe(1080);
        desiredState.ScaledSize.Width.ShouldBeLessThan(700);

        var builder = new SoftwarePipelineBuilder(
            new PipelineBuilderBaseTests.DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            video,
            audio,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(FFmpegState(HardwareAccelerationMode.None), desiredState);

        string command = Command(video, audio, result);
        command.ShouldContain("scale=");
        command.ShouldContain("pad=1920:1080");
        command.ShouldNotContain("-noautorotate");
    }

    [Test]
    public void Nvidia_Rotated_Video_Should_Decode_In_Software_And_Encode_With_Nvenc()
    {
        VideoInputFile video = RotatedInput(90);
        AudioInputFile audio = Audio();
        FrameState desiredState = DesiredState(video.VideoStreams[0]);

        var builder = new NvidiaPipelineBuilder(
            new PipelineBuilderBaseTests.DefaultFFmpegCapabilities(),
            NvidiaCapabilities(),
            HardwareAccelerationMode.Nvenc,
            video,
            audio,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(FFmpegState(HardwareAccelerationMode.Nvenc), desiredState);

        result.PipelineSteps.ShouldContain(ps => ps is EncoderH264Nvenc);

        string command = Command(video, audio, result);

        // cuda decode would skip ffmpeg's auto-rotation, so the input must be decoded in software
        command.ShouldNotContain("-hwaccel cuda");
        command.ShouldContain("h264_nvenc");
        command.ShouldContain("scale=");
        command.ShouldContain("pad=1920:1080");
    }

    [Test]
    public void Nvidia_Rotated_Hdr_Video_Should_Decode_And_Tonemap_In_Software()
    {
        var hdrStream = new VideoStream(
            0,
            VideoFormat.Hevc,
            VideoProfile.Main,
            new PixelFormatYuv420P10Le(),
            new ColorParams("tv", "bt2020nc", "arib-std-b67", "bt2020"),
            new FrameSize(1080, 1920),
            "1:1",
            "1080:1920",
            FrameRate.DefaultFrameRate,
            false,
            ScanKind.Progressive) { Rotation = 90 };
        var video = new VideoInputFile("/tmp/portrait.mp4", new List<VideoStream> { hdrStream });
        AudioInputFile audio = Audio();
        FrameState desiredState = DesiredState(hdrStream);

        var builder = new NvidiaPipelineBuilder(
            new PipelineBuilderBaseTests.DefaultFFmpegCapabilities(),
            NvidiaCapabilities(),
            HardwareAccelerationMode.Nvenc,
            video,
            audio,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(FFmpegState(HardwareAccelerationMode.Nvenc), desiredState);

        string command = Command(video, audio, result);
        command.ShouldNotContain("-hwaccel cuda");
        command.ShouldContain("tonemap");
        command.ShouldContain("h264_nvenc");
        command.ShouldContain("pad=1920:1080");
    }

    [Test]
    public void Nvidia_Unrotated_Video_Should_Still_Decode_In_Hardware()
    {
        VideoInputFile video = RotatedInput(0);
        AudioInputFile audio = Audio();
        FrameState desiredState = DesiredState(video.VideoStreams[0]);

        var builder = new NvidiaPipelineBuilder(
            new PipelineBuilderBaseTests.DefaultFFmpegCapabilities(),
            NvidiaCapabilities(),
            HardwareAccelerationMode.Nvenc,
            video,
            audio,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(FFmpegState(HardwareAccelerationMode.Nvenc), desiredState);

        string command = Command(video, audio, result);
        command.ShouldContain("-hwaccel cuda");
        command.ShouldContain("h264_nvenc");
    }
}

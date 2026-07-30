using System;
using System.Collections.Generic;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Encoder;
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

namespace ErsatzTV.FFmpeg.Tests;

[TestFixture]
public class PipelineBuilderBaseTests
{
    private readonly ILogger _logger = Substitute.For<ILogger>();

    [Test]
    public void Incorrect_Video_Codec_Should_Use_Encoder()
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
                    new FrameSize(1920, 1080),
                    "1:1",
                    "16:9",
                    FrameRate.DefaultFrameRate,
                    false,
                    ScanKind.Progressive)
            });

        var audioInputFile = new AudioInputFile(
            "/tmp/whatever.mkv",
            new List<AudioStream> { new(1, AudioFormat.Aac, 2) },
            new AudioState(
                AudioFormat.Aac,
                2,
                320,
                640,
                48,
                false,
                AudioFilter.None,
                Option<double>.None));

        var desiredState = new FrameState(
            true,
            false,
            VideoFormat.Hevc,
            VideoProfile.Main,
            VideoPreset.Unset,
            false,
            new PixelFormatYuv420P(),
            new FrameSize(1920, 1080),
            new FrameSize(1920, 1080),
            Option<FrameSize>.None,
            FFmpegFilterMode.HardwareIfPossible,
            false,
            Option<FrameRate>.None,
            2000,
            4000,
            90_000,
            false,
            false);

        var ffmpegState = new FFmpegState(
            false,
            HardwareAccelerationMode.None,
            HardwareAccelerationMode.None,
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

        var builder = new SoftwarePipelineBuilder(
            new DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            videoInputFile,
            audioInputFile,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(ffmpegState, desiredState);

        result.PipelineSteps.Count.ShouldBeGreaterThan(0);
        result.PipelineSteps.ShouldContain(ps => ps is EncoderLibx265);

        string command = PrintCommand(videoInputFile, audioInputFile, None, None, None, result);
        command.ShouldBe(
            "-nostdin -hide_banner -nostats -progress - -loglevel error -fflags +genpts+discardcorrupt+igndts -ss 00:00:01 -c:v h264 -readrate 1.05 -i /tmp/whatever.mkv -filter_complex [0:1]aresample=async=1[a] -map 0:0 -map [a] -muxdelay 0 -muxpreload 0 -movflags +faststart -flags cgop -bf 0 -sc_threshold 0 -video_track_timescale 90000 -b:v 2000k -maxrate:v 2000k -bufsize:v 4000k -c:v libx265 -tag:v hvc1 -x265-params log-level=error -c:a aac -b:a 320k -maxrate:a 320k -bufsize:a 640k -ar 48k -f mpegts -mpegts_flags +initial_discontinuity pipe:1");
    }

    [Test]
    public void Aac_6_Channel_Should_Specify_Audio_Channels()
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
                    new FrameSize(1920, 1080),
                    "1:1",
                    "16:9",
                    FrameRate.DefaultFrameRate,
                    false,
                    ScanKind.Progressive)
            });

        var audioInputFile = new AudioInputFile(
            "/tmp/whatever.mkv",
            new List<AudioStream> { new(1, AudioFormat.Aac, 6) },
            new AudioState(
                AudioFormat.Aac,
                6,
                320,
                640,
                48,
                false,
                AudioFilter.None,
                Option<double>.None));

        var desiredState = new FrameState(
            true,
            false,
            VideoFormat.Hevc,
            VideoProfile.Main,
            VideoPreset.Unset,
            false,
            new PixelFormatYuv420P(),
            new FrameSize(1920, 1080),
            new FrameSize(1920, 1080),
            Option<FrameSize>.None,
            FFmpegFilterMode.HardwareIfPossible,
            false,
            Option<FrameRate>.None,
            2000,
            4000,
            90_000,
            false,
            false);

        var ffmpegState = new FFmpegState(
            false,
            HardwareAccelerationMode.None,
            HardwareAccelerationMode.None,
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

        var builder = new SoftwarePipelineBuilder(
            new DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            videoInputFile,
            audioInputFile,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(ffmpegState, desiredState);

        result.PipelineSteps.Count.ShouldBeGreaterThan(0);
        result.PipelineSteps.ShouldContain(ps => ps is EncoderLibx265);

        string command = PrintCommand(videoInputFile, audioInputFile, None, None, None, result);
        command.ShouldBe(
            "-nostdin -hide_banner -nostats -progress - -loglevel error -fflags +genpts+discardcorrupt+igndts -ss 00:00:01 -c:v h264 -readrate 1.05 -i /tmp/whatever.mkv -filter_complex [0:1]aresample=async=1[a] -map 0:0 -map [a] -muxdelay 0 -muxpreload 0 -movflags +faststart -flags cgop -bf 0 -sc_threshold 0 -video_track_timescale 90000 -b:v 2000k -maxrate:v 2000k -bufsize:v 4000k -c:v libx265 -tag:v hvc1 -x265-params log-level=error -c:a aac -ac 6 -b:a 320k -maxrate:a 320k -bufsize:a 640k -ar 48k -f mpegts -mpegts_flags +initial_discontinuity pipe:1");
    }

    [Test]
    public void Concat_Test()
    {
        var resolution = new FrameSize(1920, 1080);
        var concatInputFile = new ConcatInputFile("http://localhost:8080/ffmpeg/concat/1", resolution);

        var builder = new SoftwarePipelineBuilder(
            new DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            None,
            None,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Concat(concatInputFile, FFmpegState.Concat(false, "Some Channel"));

        result.PipelineSteps.Count.ShouldBeGreaterThan(0);

        string command = PrintCommand(None, None, None, concatInputFile, None, result);

        command.ShouldBe(
            "-nostdin -hide_banner -nostats -loglevel error -fflags +genpts+discardcorrupt+igndts -f concat -safe 0 -protocol_whitelist file,http,tcp,https,tcp,tls -probesize 32 -readrate 1.0 -stream_loop -1 -i http://localhost:8080/ffmpeg/concat/1 -muxdelay 0 -muxpreload 0 -movflags +faststart -flags cgop -sc_threshold 0 -c copy -map_metadata -1 -metadata service_provider=\"ErsatzTV\" -metadata service_name=\"Some Channel\" -f mpegts -mpegts_flags +initial_discontinuity pipe:1");
    }

    [Test]
    public void Wrap_Segmenter_Test()
    {
        var resolution = new FrameSize(1920, 1080);
        var concatInputFile = new ConcatInputFile(
            "http://localhost:8080/iptv/channel/1.m3u8?mode=segmenter",
            resolution);

        var builder = new SoftwarePipelineBuilder(
            new DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            None,
            None,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.WrapSegmenter(concatInputFile, FFmpegState.Concat(false, "Some Channel"));

        result.PipelineSteps.Count.ShouldBeGreaterThan(0);

        string command = PrintCommand(None, None, None, concatInputFile, None, result);

        command.ShouldBe(
            "-nostdin -threads 1 -hide_banner -loglevel error -nostats -fflags +genpts+discardcorrupt+igndts -readrate 1.0 -i http://localhost:8080/iptv/channel/1.m3u8?mode=segmenter -map 0 -c copy -metadata service_provider=\"ErsatzTV\" -metadata service_name=\"Some Channel\" -f mpegts pipe:1");
    }

    [Test]
    public void HlsDirect_Test()
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
                    new FrameSize(1920, 1080),
                    "1:1",
                    "16:9",
                    FrameRate.DefaultFrameRate,
                    false,
                    ScanKind.Interlaced)
            });

        var audioInputFile = new AudioInputFile(
            "/tmp/whatever.mkv",
            new List<AudioStream> { new(1, AudioFormat.Aac, 2) },
            new AudioState(
                AudioFormat.Copy,
                None,
                None,
                None,
                None,
                false,
                AudioFilter.None,
                Option<double>.None));

        var desiredState = new FrameState(
            true,
            false,
            VideoFormat.Copy,
            VideoProfile.Main,
            VideoPreset.Unset,
            false,
            Option<IPixelFormat>.None,
            new FrameSize(1920, 1080),
            new FrameSize(1920, 1080),
            Option<FrameSize>.None,
            FFmpegFilterMode.HardwareIfPossible,
            false,
            Option<FrameRate>.None,
            2000,
            4000,
            90_000,
            false,
            false);

        var ffmpegState = new FFmpegState(
            false,
            HardwareAccelerationMode.None,
            HardwareAccelerationMode.None,
            Option<string>.None,
            Option<string>.None,
            Option<TimeSpan>.None,
            Option<TimeSpan>.None,
            false,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            OutputFormatKind.Mp4,
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

        var builder = new SoftwarePipelineBuilder(
            new DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            videoInputFile,
            audioInputFile,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(ffmpegState, desiredState);

        result.PipelineSteps.Count.ShouldBeGreaterThan(0);
        result.PipelineSteps.ShouldContain(ps => ps is EncoderCopyVideo);
        result.PipelineSteps.ShouldContain(ps => ps is EncoderCopyAudio);
        videoInputFile.InputOptions.ShouldContain(io => io is ReadrateInputOption);

        string command = PrintCommand(videoInputFile, audioInputFile, None, None, None, result);

        // 0.4.0 reference: "-nostdin -threads 1 -hide_banner -loglevel error -nostats -fflags +genpts+discardcorrupt+igndts -re -ss 00:14:33.6195516 -i /tmp/whatever.mkv -map 0:0 -map 0:a -c:v copy -flags cgop -sc_threshold 0 -c:a copy -movflags +faststart -metadata service_provider="ErsatzTV" -metadata service_name="ErsatzTV" -t 00:06:39.6934484 -f mpegts -mpegts_flags +initial_discontinuity pipe:1"
        command.ShouldBe(
            "-nostdin -hide_banner -nostats -progress - -loglevel error -fflags +genpts+discardcorrupt+igndts -readrate 1.0 -i /tmp/whatever.mkv -map 0:0 -map 0:1 -muxdelay 0 -muxpreload 0 -movflags +faststart+frag_keyframe+separate_moof+omit_tfhd_offset+empty_moov+delay_moov -c:v copy -c:a copy -f mp4 pipe:1");
    }

    [Test]
    public void HlsDirect_Test_All_Audio_Streams()
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
                    new FrameSize(1920, 1080),
                    "1:1",
                    "16:9",
                    FrameRate.DefaultFrameRate,
                    false,
                    ScanKind.Progressive)
            });

        Option<AudioInputFile> audioInputFile = Option<AudioInputFile>.None;

        var desiredState = new FrameState(
            true,
            false,
            VideoFormat.Copy,
            VideoProfile.Main,
            VideoPreset.Unset,
            false,
            new PixelFormatYuv420P(),
            new FrameSize(1920, 1080),
            new FrameSize(1920, 1080),
            Option<FrameSize>.None,
            FFmpegFilterMode.HardwareIfPossible,
            false,
            Option<FrameRate>.None,
            2000,
            4000,
            90_000,
            false,
            false);

        var ffmpegState = new FFmpegState(
            false,
            HardwareAccelerationMode.None,
            HardwareAccelerationMode.None,
            Option<string>.None,
            Option<string>.None,
            Option<TimeSpan>.None,
            Option<TimeSpan>.None,
            false,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            OutputFormatKind.Mp4,
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

        var builder = new SoftwarePipelineBuilder(
            new DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            videoInputFile,
            audioInputFile,
            None,
            None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);
        FFmpegPipeline result = builder.Build(ffmpegState, desiredState);

        result.PipelineSteps.Count.ShouldBeGreaterThan(0);
        result.PipelineSteps.ShouldContain(ps => ps is EncoderCopyVideo);
        result.PipelineSteps.ShouldContain(ps => ps is EncoderCopyAudio);

        string command = PrintCommand(videoInputFile, audioInputFile, None, None, None, result);

        command.ShouldBe(
            "-nostdin -hide_banner -nostats -progress - -loglevel error -fflags +genpts+discardcorrupt+igndts -readrate 1.0 -i /tmp/whatever.mkv -map 0:0 -map 0:a -muxdelay 0 -muxpreload 0 -movflags +faststart+frag_keyframe+separate_moof+omit_tfhd_offset+empty_moov+delay_moov -c:v copy -c:a copy -f mp4 pipe:1");
    }

    [Test]
    public void Resize_Image_Test()
    {
        var height = 200;

        var videoInputFile = new VideoInputFile(
            "/test/input/file.png",
            new List<VideoStream>
            {
                new(
                    0,
                    string.Empty,
                    string.Empty,
                    Option<IPixelFormat>.None,
                    ColorParams.Default,
                    FrameSize.Unknown,
                    string.Empty,
                    string.Empty,
                    Option<FrameRate>.None,
                    true,
                    ScanKind.Progressive)
            });

        var pipelineBuilder = new SoftwarePipelineBuilder(
            new DefaultFFmpegCapabilities(),
            HardwareAccelerationMode.None,
            videoInputFile,
            Option<AudioInputFile>.None,
            Option<WatermarkInputFile>.None,
            Option<SubtitleInputFile>.None,
            None,
            Option<GraphicsEngineInput>.None,
            "",
            "",
            _logger);

        FFmpegPipeline result = pipelineBuilder.Resize("/test/output/file.jpg", new FrameSize(-1, height));

        string command = PrintCommand(videoInputFile, None, None, None, None, result);

        command.ShouldBe(
            "-nostdin -hide_banner -nostats -loglevel error -i /test/input/file.png -vf scale=-1:200:force_original_aspect_ratio=decrease /test/output/file.jpg");
    }

    private static string PrintCommand(
        Option<VideoInputFile> videoInputFile,
        Option<AudioInputFile> audioInputFile,
        Option<WatermarkInputFile> watermarkInputFile,
        Option<ConcatInputFile> concatInputFile,
        Option<GraphicsEngineInput> graphicsEngineInput,
        FFmpegPipeline pipeline)
    {
        IList<string> arguments = CommandGenerator.GenerateArguments(
            videoInputFile,
            audioInputFile,
            watermarkInputFile,
            concatInputFile,
            graphicsEngineInput,
            pipeline.PipelineSteps,
            pipeline.IsIntelVaapiOrQsv);

        var command = string.Join(" ", arguments);

        Console.WriteLine($"Generated command: ffmpeg {string.Join(" ", arguments)}");

        return command;
    }

    public class DefaultFFmpegCapabilities() : FFmpegCapabilities(
        string.Empty,
        new System.Collections.Generic.HashSet<string>(),
        new System.Collections.Generic.HashSet<string>(),
        new System.Collections.Generic.HashSet<string>(),
        new System.Collections.Generic.HashSet<string>(),
        new System.Collections.Generic.HashSet<string>(),
        new System.Collections.Generic.HashSet<string>(),
        new System.Collections.Generic.Dictionary<string, IReadOnlySet<string>>());
}

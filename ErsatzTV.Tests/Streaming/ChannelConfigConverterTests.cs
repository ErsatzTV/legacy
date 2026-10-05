using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Next.Config;
using ErsatzTV.FFmpeg;
using LanguageExt;
using NUnit.Framework;
using Testably.Abstractions;
using Shouldly;
using ResolutionViewModel = ErsatzTV.Application.Resolutions.ResolutionViewModel;

namespace ErsatzTV.Tests.Streaming;

[TestFixture]
public class ChannelConfigConverterTests
{
    private static readonly ChannelViewModel Channel = new(
        Id: 1,
        Number: "1",
        Name: "Test",
        Group: null!,
        Categories: null!,
        FFmpegProfileId: 1,
        SlugSeconds: null,
        Logo: null!,
        StreamSelectorMode: default,
        StreamSelector: null!,
        PreferredAudioLanguageCode: null!,
        PreferredAudioTitle: null!,
        PlayoutSource: default,
        PlayoutMode: default,
        MirrorSourceChannelId: null,
        PlayoutOffset: null,
        StreamingEngine: StreamingEngine.Next,
        NextEngineTextSubtitleMode: default,
        StreamingMode: StreamingMode.HttpLiveStreamingSegmenter,
        WatermarkId: null,
        FallbackFillerId: null,
        PlayoutCount: 0,
        PreferredSubtitleLanguageCode: null!,
        SubtitleMode: default,
        MusicVideoCreditsMode: default,
        MusicVideoCreditsTemplate: null!,
        SongVideoMode: default,
        TranscodeMode: default,
        IdleBehavior: default,
        IsEnabled: true,
        ShowInEpg: true);

    private static readonly FFmpegProfileViewModel Profile = new(
        Id: 1,
        Name: "Test",
        ThreadCount: 0,
        NormalizeAudio: true,
        NormalizeVideo: true,
        HardwareAcceleration: HardwareAccelerationKind.None,
        VaapiDisplay: null!,
        VaapiDriver: VaapiDriver.Default,
        VaapiDevice: null!,
        QsvExtraHardwareFrames: null,
        Resolution: new ResolutionViewModel(1, "1080p", 1920, 1080, false),
        ScalingBehavior: ScalingBehavior.ScaleAndPad,
        PadMode: default,
        VideoFormat: FFmpegProfileVideoFormat.H264,
        VideoProfile: null!,
        VideoPreset: null!,
        AllowBFrames: false,
        BitDepth: FFmpegProfileBitDepth.EightBit,
        VideoBitrate: 2000,
        VideoBufferSize: 4000,
        TonemapAlgorithm: default,
        AudioFormat: FFmpegProfileAudioFormat.Aac,
        AudioBitrate: 192,
        AudioBufferSize: 384,
        NormalizeLoudnessMode: NormalizeLoudnessMode.LoudNorm,
        TargetLoudness: -16,
        AudioChannels: 2,
        AudioSampleRate: 48,
        NormalizeFramerate: false,
        NormalizeColors: true,
        DeinterlaceVideo: false);

    [Test]
    public async Task Copy_profile_should_copy_with_transcode_fallback()
    {
        FFmpegProfileViewModel profile = Profile with
        {
            NormalizeAudio = false,
            NormalizeVideo = false,
            AudioFormat = FFmpegProfileAudioFormat.Copy,
            VideoFormat = FFmpegProfileVideoFormat.Copy
        };

        ChannelConfig config = await Convert(profile);

        config.Version.ShouldBe(ChannelConfigConverter.ChannelConfigVersion);
        config.Normalization.Audio.Mode.ShouldBe(StreamMode.Copy);
        config.Normalization.Audio.Format.ShouldBe(AudioFormat.Aac);
        config.Normalization.Video.Mode.ShouldBe(StreamMode.Copy);
        config.Normalization.Video.Format.ShouldBe(VideoFormat.H264);
        config.Normalization.Video.Width.ShouldBe(1920);
    }

    [Test]
    public async Task Audio_copy_should_not_normalize_loudness()
    {
        FFmpegProfileViewModel profile = Profile with
        {
            NormalizeAudio = false,
            AudioFormat = FFmpegProfileAudioFormat.Copy
        };

        ChannelConfig config = await Convert(profile);

        config.Normalization.Audio.Mode.ShouldBe(StreamMode.Copy);
        config.Normalization.Audio.NormalizeLoudness.ShouldBeNull();
        config.Normalization.Audio.Loudness.ShouldBeNull();
        config.Normalization.Video.Mode.ShouldBe(StreamMode.Transcode);
    }

    [Test]
    public async Task Transcode_profile_should_transcode()
    {
        ChannelConfig config = await Convert(Profile);

        config.Normalization.Audio.Mode.ShouldBe(StreamMode.Transcode);
        config.Normalization.Audio.NormalizeLoudness.ShouldBe(true);
        config.Normalization.Video.Mode.ShouldBe(StreamMode.Transcode);
    }

    [Test]
    public async Task No_target_framerate_should_pass_source_rate_through()
    {
        ChannelConfig config = await Convert(Profile);

        config.Normalization.Video.FrameRate.ShouldBeNull();
    }

    [Test]
    public async Task Target_framerate_should_set_frame_rate()
    {
        ChannelConfig config = await Convert(Profile, new FrameRate("24000/1001"));

        config.Normalization.Video.FrameRate.ShouldBe("24000/1001");
    }

    [TestCase("24000/1001", "24000/1001")]
    [TestCase("25/1", "25/1")]
    [TestCase("30", "30")]
    [TestCase("23.976", "24000/1001")]
    [TestCase("23.98", "24000/1001")]
    [TestCase("29.97", "30000/1001")]
    [TestCase("59.94", "60000/1001")]
    [TestCase("25.00", "25")]
    [TestCase("12.5", "12500/1000")]
    public void ToNextFrameRate_should_convert_to_rational(string input, string expected) =>
        ChannelConfigConverter.ToNextFrameRate(new FrameRate(input)).ShouldBe(Option<string>.Some(expected));

    [TestCase("")]
    [TestCase("0/0")]
    [TestCase("30/0")]
    [TestCase("1/2")]
    [TestCase("0.5")]
    [TestCase("241")]
    [TestCase("1000/1")]
    [TestCase("abc")]
    [TestCase("-30")]
    public void ToNextFrameRate_should_reject_unusable_rates(string input) =>
        ChannelConfigConverter.ToNextFrameRate(new FrameRate(input)).IsNone.ShouldBeTrue();

    private static Task<ChannelConfig> Convert(FFmpegProfileViewModel profile) =>
        Convert(profile, Option<FrameRate>.None);

    private static Task<ChannelConfig> Convert(FFmpegProfileViewModel profile, Option<FrameRate> targetFramerate) =>
        new ChannelConfigConverter(new EmptyConfigElementRepository(), new RealFileSystem())
            .ToNext(Channel, profile, targetFramerate, CancellationToken.None);

    private sealed class EmptyConfigElementRepository : IConfigElementRepository
    {
        public Task<Unit> Upsert<T>(ConfigElementKey configElementKey, T value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Option<ConfigElement>> GetConfigElement(ConfigElementKey key, CancellationToken cancellationToken) =>
            Task.FromResult(Option<ConfigElement>.None);

        public Task<Option<T>> GetValue<T>(ConfigElementKey key, CancellationToken cancellationToken) =>
            Task.FromResult(Option<T>.None);

        public Task Delete(ConfigElement configElement, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Unit> Delete(ConfigElementKey configElementKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

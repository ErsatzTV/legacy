using System.IO.Abstractions;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Next.Config;
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

    private static Task<ChannelConfig> Convert(FFmpegProfileViewModel profile) =>
        new ChannelConfigConverter(new EmptyConfigElementRepository(), new RealFileSystem())
            .ToNext(Channel, profile, CancellationToken.None);

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

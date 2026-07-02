using System.Collections.Immutable;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.Environment;
using ErsatzTV.FFmpeg.Format;
using ErsatzTV.FFmpeg.OutputFormat;
using ErsatzTV.FFmpeg.Pipeline;
using ErsatzTV.FFmpeg.Preset;
using ErsatzTV.FFmpeg.State;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MediaStream = ErsatzTV.Core.Domain.MediaStream;

namespace ErsatzTV.Core.FFmpeg;

public class FFmpegLibraryProcessService : IFFmpegProcessService
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IGraphicsElementLoader _graphicsElementLoader;
    private readonly IMemoryCache _memoryCache;
    private readonly IMpegTsScriptService _mpegTsScriptService;
    private readonly ILocalStatisticsProvider _localStatisticsProvider;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ICustomStreamSelector _customStreamSelector;
    private readonly FFmpegProcessService _ffmpegProcessService;
    private readonly IFFmpegStreamSelector _ffmpegStreamSelector;
    private readonly ILogger<FFmpegLibraryProcessService> _logger;
    private readonly IPipelineBuilderFactory _pipelineBuilderFactory;
    private readonly ITempFilePool _tempFilePool;

    public FFmpegLibraryProcessService(
        FFmpegProcessService ffmpegProcessService,
        IFFmpegStreamSelector ffmpegStreamSelector,
        ICustomStreamSelector customStreamSelector,
        ITempFilePool tempFilePool,
        IPipelineBuilderFactory pipelineBuilderFactory,
        IConfigElementRepository configElementRepository,
        IGraphicsElementLoader graphicsElementLoader,
        IMemoryCache memoryCache,
        IMpegTsScriptService mpegTsScriptService,
        ILocalStatisticsProvider localStatisticsProvider,
        IMediaItemRepository mediaItemRepository,
        ILocalFileSystem localFileSystem,
        ILogger<FFmpegLibraryProcessService> logger)
    {
        _ffmpegProcessService = ffmpegProcessService;
        _ffmpegStreamSelector = ffmpegStreamSelector;
        _customStreamSelector = customStreamSelector;
        _tempFilePool = tempFilePool;
        _pipelineBuilderFactory = pipelineBuilderFactory;
        _configElementRepository = configElementRepository;
        _graphicsElementLoader = graphicsElementLoader;
        _memoryCache = memoryCache;
        _mpegTsScriptService = mpegTsScriptService;
        _localStatisticsProvider = localStatisticsProvider;
        _mediaItemRepository = mediaItemRepository;
        _localFileSystem = localFileSystem;
        _logger = logger;
    }

    public async Task<PlayoutItemResult> ForPlayoutItem(
        string ffmpegPath,
        string ffprobePath,
        bool saveReports,
        Channel channel,
        MediaItemVideoVersion videoVersion,
        MediaItemAudioVersion audioVersion,
        string videoPath,
        string audioPath,
        Func<FFmpegPlaybackSettings, Task<List<Subtitle>>> getSubtitles,
        string preferredAudioLanguage,
        string preferredAudioTitle,
        string preferredSubtitleLanguage,
        ChannelSubtitleMode subtitleMode,
        DateTimeOffset start,
        DateTimeOffset finish,
        DateTimeOffset now,
        TimeSpan originalContentDuration,
        List<WatermarkOptions> watermarks,
        List<PlayoutItemGraphicsElement> graphicsElements,
        string vaapiDisplay,
        VaapiDriver vaapiDriver,
        string vaapiDevice,
        Option<int> qsvExtraHardwareFrames,
        bool hlsRealtime,
        StreamInputKind streamInputKind,
        FillerKind fillerKind,
        TimeSpan inPoint,
        DateTimeOffset channelStartTime,
        TimeSpan ptsOffset,
        Option<FrameRate> targetFramerate,
        Option<string> customReportsFolder,
        Action<FFmpegPipeline> pipelineAction,
        bool canProxy,
        CancellationToken cancellationToken)
    {
        MediaStream videoStream = await _ffmpegStreamSelector.SelectVideoStream(videoVersion.MediaVersion);

        // we cannot burst live input
        hlsRealtime = hlsRealtime || streamInputKind is StreamInputKind.Live;

        FFmpegPlaybackSettings playbackSettings = FFmpegPlaybackSettingsCalculator.CalculateSettings(
            channel.StreamingMode,
            channel.FFmpegProfile,
            videoVersion.MediaVersion,
            videoStream,
            start,
            now,
            inPoint,
            hlsRealtime,
            streamInputKind,
            targetFramerate);

        List<Subtitle> allSubtitles = await getSubtitles(playbackSettings);

        Option<MediaStream> maybeAudioStream = Option<MediaStream>.None;
        Option<Subtitle> maybeSubtitle = Option<Subtitle>.None;

        if (channel.StreamSelectorMode is ChannelStreamSelectorMode.Custom)
        {
            StreamSelectorResult result = await _customStreamSelector.SelectStreams(
                channel,
                start,
                audioVersion,
                allSubtitles,
                shouldLogMessages: true);
            maybeAudioStream = result.AudioStream;
            maybeSubtitle = result.Subtitle;

            if (maybeAudioStream.IsNone)
            {
                _logger.LogWarning(
                    "No audio stream found using custom stream selector {StreamSelector}; will use default stream selection logic",
                    channel.StreamSelector);
            }
        }

        if (channel.StreamSelectorMode is ChannelStreamSelectorMode.Default || maybeAudioStream.IsNone)
        {
            maybeAudioStream =
                await _ffmpegStreamSelector.SelectAudioStream(
                    audioVersion,
                    channel.StreamingMode,
                    channel,
                    preferredAudioLanguage,
                    preferredAudioTitle,
                    shouldLogMessages: true,
                    cancellationToken);

            maybeSubtitle =
                await _ffmpegStreamSelector.SelectSubtitleStream(
                    allSubtitles.ToImmutableList(),
                    channel,
                    preferredSubtitleLanguage,
                    subtitleMode,
                    shouldLogMessages: true,
                    cancellationToken);
        }

        if (channel.StreamSelectorMode is ChannelStreamSelectorMode.Troubleshooting && maybeSubtitle.IsNone)
        {
            maybeSubtitle = allSubtitles.HeadOrNone();
        }

        if (canProxy)
        {
            foreach (Subtitle subtitle in maybeSubtitle)
            {
                if (subtitle.SubtitleKind == SubtitleKind.Sidecar || subtitle is
                        { SubtitleKind: SubtitleKind.Embedded, IsImage: false, IsExtracted: true })
                {
                    // proxy to avoid dealing with escaping
                    subtitle.Path = $"http://localhost:{Settings.StreamingPort}/media/subtitle/{subtitle.Id}";

                    foreach (TimeSpan seek in playbackSettings.StreamSeek)
                    {
                        subtitle.Path += $"?seekToMs={(int)seek.TotalMilliseconds}";
                    }
                }
            }
        }

        string audioFormat = playbackSettings.AudioFormat switch
        {
            FFmpegProfileAudioFormat.Aac => AudioFormat.Aac,
            FFmpegProfileAudioFormat.AacLatm => AudioFormat.AacLatm,
            FFmpegProfileAudioFormat.Ac3 => AudioFormat.Ac3,
            FFmpegProfileAudioFormat.Copy => AudioFormat.Copy,
            _ => throw new ArgumentOutOfRangeException($"unexpected audio format {playbackSettings.VideoFormat}")
        };

        var audioState = new AudioState(
            audioFormat,
            playbackSettings.AudioChannels,
            playbackSettings.AudioBitrate,
            playbackSettings.AudioBufferSize,
            playbackSettings.AudioSampleRate,
            audioFormat != AudioFormat.Copy && videoPath == audioPath,
            playbackSettings.NormalizeLoudnessMode switch
            {
                NormalizeLoudnessMode.LoudNorm => AudioFilter.LoudNorm,
                _ => AudioFilter.None
            },
            playbackSettings.TargetLoudness);

        // don't log generated images, or hls direct, which are expected to have unknown format
        bool isUnknownPixelFormatExpected =
            videoPath != audioPath || channel.StreamingMode == StreamingMode.HttpLiveStreamingDirect;
        ILogger<FFmpegLibraryProcessService> pixelFormatLogger = isUnknownPixelFormatExpected ? null : _logger;

        IPixelFormat pixelFormat = await AvailablePixelFormats
            .ForPixelFormat(videoStream.PixelFormat, pixelFormatLogger)
            .IfNoneAsync(() =>
            {
                return videoStream.BitsPerRawSample switch
                {
                    8 => new PixelFormatYuv420P(),
                    10 => new PixelFormatYuv420P10Le(),
                    _ => new PixelFormatUnknown(videoStream.BitsPerRawSample)
                };
            });

        ScanKind scanKind = ScanKind.Progressive;
        if (playbackSettings.Deinterlace)
        {
            scanKind = await ProbeScanKind(ffmpegPath, videoVersion.MediaItem, cancellationToken);
        }

        HardwareAccelerationMode hwAccel = GetHardwareAccelerationMode(playbackSettings);

        // QSV may have sync issues with h264 files that have multiple profiles
        // check and flag here so software decoding can be used if needed
        bool hasMultipleProfiles = false;
        if (hwAccel is HardwareAccelerationMode.Qsv && videoStream.Codec is VideoFormat.H264)
        {
            hasMultipleProfiles = await ProbeHasMultipleProfiles(ffmpegPath, videoVersion.MediaItem, cancellationToken);
        }

        var ffmpegVideoStream = new VideoStream(
            videoStream.Index,
            videoStream.Codec,
            videoStream.Profile,
            Some(pixelFormat),
            new ColorParams(
                videoStream.ColorRange,
                videoStream.ColorSpace,
                videoStream.ColorTransfer,
                videoStream.ColorPrimaries),
            new FrameSize(videoVersion.MediaVersion.Width, videoVersion.MediaVersion.Height),
            videoVersion.MediaVersion.SampleAspectRatio,
            videoVersion.MediaVersion.DisplayAspectRatio,
            new FrameRate(videoVersion.MediaVersion.RFrameRate),
            videoPath != audioPath, // still image when paths are different
            scanKind)
        {
            HasMultipleProfiles = hasMultipleProfiles
        };

        var videoInputFile = new VideoInputFile(
            videoPath,
            new List<VideoStream> { ffmpegVideoStream },
            streamInputKind);

        Option<AudioInputFile> audioInputFile = maybeAudioStream.Map(audioStream =>
        {
            var ffmpegAudioStream = new AudioStream(audioStream.Index, audioStream.Codec, audioStream.Channels);
            return new AudioInputFile(audioPath, new List<AudioStream> { ffmpegAudioStream }, audioState);
        });

        // when no audio streams are available, use null audio source
        if (!audioVersion.MediaVersion.Streams.Any(s => s.MediaStreamKind is MediaStreamKind.Audio))
        {
            audioInputFile = new NullAudioInputFile(audioState with { PadAudio = playbackSettings.PadAudio });
        }

        OutputFormatKind outputFormat = OutputFormatKind.MpegTs;
        switch (channel.StreamingMode)
        {
            case StreamingMode.HttpLiveStreamingSegmenter:
                outputFormat = OutputFormatKind.Hls;
                break;
            case StreamingMode.HttpLiveStreamingDirect:
            {
                // use mpeg-ts by default
                outputFormat = OutputFormatKind.MpegTs;

                // override with setting if applicable
                Option<OutputFormatKind> maybeOutputFormat = await _configElementRepository
                    .GetValue<OutputFormatKind>(ConfigElementKey.FFmpegHlsDirectOutputFormat, cancellationToken);
                foreach (OutputFormatKind of in maybeOutputFormat)
                {
                    outputFormat = of;
                }

                break;
            }
        }

        Option<string> subtitleLanguage = Option<string>.None;
        Option<string> subtitleTitle = Option<string>.None;

        Option<SubtitleInputFile> subtitleInputFile = maybeSubtitle.Map<Option<SubtitleInputFile>>(subtitle =>
        {
            if (channel.StreamingMode != StreamingMode.HttpLiveStreamingDirect && !subtitle.IsImage &&
                subtitle.SubtitleKind == SubtitleKind.Embedded &&
                (!subtitle.IsExtracted || string.IsNullOrWhiteSpace(subtitle.Path)))
            {
                _logger.LogWarning("Subtitles are not yet available for this item");
                return None;
            }

            var ffmpegSubtitleStream = new ErsatzTV.FFmpeg.MediaStream(
                subtitle.IsImage ? subtitle.StreamIndex : 0,
                subtitle.Codec,
                StreamKind.Video);

            string subtitlePath = subtitle.Path;
            if (!canProxy && !subtitle.IsImage && subtitle.IsExtracted)
            {
                subtitlePath = Path.Combine(FileSystemLayout.SubtitleCacheFolder, subtitlePath);
            }

            string path = subtitle.IsImage ? videoPath : subtitlePath;

            SubtitleMethod method = SubtitleMethod.Burn;
            if (channel.StreamingMode == StreamingMode.HttpLiveStreamingDirect)
            {
                method = (outputFormat, subtitle.SubtitleKind, subtitle.Codec) switch
                {
                    // mkv supports all subtitle codecs, maybe?
                    (OutputFormatKind.Mkv, SubtitleKind.Embedded, _) => SubtitleMethod.Copy,

                    // MP4 supports vobsub
                    (OutputFormatKind.Mp4, SubtitleKind.Embedded, "dvdsub" or "dvd_subtitle" or "vobsub") =>
                        SubtitleMethod.Copy,

                    // MP4 does not support PGS
                    (OutputFormatKind.Mp4, SubtitleKind.Embedded, "pgs" or "pgssub" or "hdmv_pgs_subtitle") =>
                        SubtitleMethod.None,

                    // ignore text subtitles for now
                    _ => SubtitleMethod.None
                };

                if (method == SubtitleMethod.None)
                {
                    return None;
                }

                // hls direct won't use extracted embedded subtitles
                if (subtitle.SubtitleKind == SubtitleKind.Embedded)
                {
                    path = videoPath;
                    ffmpegSubtitleStream = ffmpegSubtitleStream with { Index = subtitle.StreamIndex };
                }
            }

            if (method == SubtitleMethod.Copy)
            {
                subtitleLanguage = Optional(subtitle.Language);
                subtitleTitle = Optional(subtitle.Title);
            }

            return new SubtitleInputFile(
                path,
                new List<ErsatzTV.FFmpeg.MediaStream> { ffmpegSubtitleStream },
                method);
        }).Flatten();

        Option<WatermarkInputFile> watermarkInputFile = Option<WatermarkInputFile>.None;
        Option<GraphicsEngineInput> graphicsEngineInput = Option<GraphicsEngineInput>.None;
        Option<GraphicsEngineContext> graphicsEngineContext = Option<GraphicsEngineContext>.None;
        List<GraphicsElementContext> graphicsElementContexts = [];

        // use ffmpeg for single permanent watermark, graphics engine for all others
        if (graphicsElements.Count == 0 && watermarks.Count == 1 && watermarks.All(wm => wm.Watermark.Mode is ChannelWatermarkMode.Permanent))
        {
            foreach (var wm in watermarks)
            {
                List<VideoStream> videoStreams =
                [
                    new(
                        await wm.ImageStreamIndex.IfNoneAsync(0),
                        "unknown",
                        string.Empty,
                        new PixelFormatUnknown(),
                        ColorParams.Default,
                        new FrameSize(1, 1),
                        string.Empty,
                        string.Empty,
                        Option<FrameRate>.None,
                        !await IsWatermarkAnimated(ffprobePath, wm.ImagePath),
                        ScanKind.Progressive)
                ];

                var state = new WatermarkState(
                    None,
                    wm.Watermark.Location,
                    wm.Watermark.Size,
                    wm.Watermark.WidthPercent,
                    wm.Watermark.HorizontalMarginPercent,
                    wm.Watermark.VerticalMarginPercent,
                    wm.Watermark.Opacity,
                    wm.Watermark.PlaceWithinSourceContent);

                watermarkInputFile = new WatermarkInputFile(wm.ImagePath, videoStreams, state);
            }
        }
        else
        {
            graphicsElementContexts.AddRange(watermarks.Map(wm => new WatermarkElementContext(wm)));
        }

        string videoFormat = GetVideoFormat(playbackSettings);
        Option<string> maybeVideoProfile = GetVideoProfile(videoFormat, channel.FFmpegProfile.VideoProfile);
        Option<string> maybeVideoPreset = GetVideoPreset(
            hwAccel,
            videoFormat,
            channel.FFmpegProfile.VideoPreset,
            FFmpegLibraryHelper.MapBitDepth(channel.FFmpegProfile.BitDepth));

        Option<string> hlsPlaylistPath = outputFormat is OutputFormatKind.Hls or OutputFormatKind.HlsMp4
            ? Path.Combine(FileSystemLayout.TranscodeFolder, channel.Number, "live.m3u8")
            : Option<string>.None;

        long nowSeconds = now.ToUnixTimeSeconds();

        Option<string> hlsSegmentTemplate = outputFormat switch
        {
            OutputFormatKind.Hls => Path.Combine(FileSystemLayout.TranscodeFolder, channel.Number, "live%06d.ts"),
            OutputFormatKind.HlsMp4 => Path.Combine(
                FileSystemLayout.TranscodeFolder,
                channel.Number,
                $"live_{nowSeconds}_%06d.m4s"),
            _ => Option<string>.None
        };

        Option<string> hlsInitTemplate = outputFormat switch
        {
            OutputFormatKind.HlsMp4 => $"{nowSeconds}_init.mp4",
            _ =>  Option<string>.None
        };

        Option<string> hlsSegmentOptions = Option<string>.None;
        if (outputFormat is OutputFormatKind.Hls)
        {
            string options = string.Empty;

            if (ptsOffset == TimeSpan.Zero)
            {
                options += "+initial_discontinuity";
            }

            if (audioFormat == AudioFormat.AacLatm)
            {
                options += "+latm";
            }

            if (!string.IsNullOrWhiteSpace(options))
            {
                hlsSegmentOptions = $"mpegts_flags={options}";
            }
        }

        FrameSize scaledSize = ffmpegVideoStream.SquarePixelFrameSize(
            new FrameSize(channel.FFmpegProfile.Resolution.Width, channel.FFmpegProfile.Resolution.Height));

        var paddedSize = new FrameSize(
            channel.FFmpegProfile.Resolution.Width,
            channel.FFmpegProfile.Resolution.Height);

        Option<FrameSize> cropSize = Option<FrameSize>.None;

        if (channel.FFmpegProfile.ScalingBehavior is ScalingBehavior.Stretch)
        {
            scaledSize = paddedSize;
        }

        if (channel.FFmpegProfile.ScalingBehavior is ScalingBehavior.Crop)
        {
            bool isTooSmallToCrop = videoVersion.MediaVersion.Height < channel.FFmpegProfile.Resolution.Height ||
                                    videoVersion.MediaVersion.Width < channel.FFmpegProfile.Resolution.Width;

            // if any dimension is smaller than the crop, scale beyond the crop (beyond the target resolution)
            if (isTooSmallToCrop)
            {
                foreach (IDisplaySize size in playbackSettings.ScaledSize)
                {
                    scaledSize = new FrameSize(size.Width, size.Height);
                }

                paddedSize = scaledSize;
            }
            else
            {
                paddedSize = ffmpegVideoStream.SquarePixelFrameSizeForCrop(
                    new FrameSize(channel.FFmpegProfile.Resolution.Width, channel.FFmpegProfile.Resolution.Height));
            }

            cropSize = new FrameSize(
                channel.FFmpegProfile.Resolution.Width,
                channel.FFmpegProfile.Resolution.Height);
        }

        var desiredState = new FrameState(
            playbackSettings.RealtimeOutput,
            InfiniteLoop: false,
            videoFormat,
            maybeVideoProfile,
            maybeVideoPreset,
            channel.FFmpegProfile.AllowBFrames,
            Optional(playbackSettings.PixelFormat),
            scaledSize,
            paddedSize,
            cropSize,
            channel.FFmpegProfile.PadMode is FilterMode.HardwareIfPossible
                ? FFmpegFilterMode.HardwareIfPossible
                : FFmpegFilterMode.Software,
            IsAnamorphic: false,
            playbackSettings.FrameRate,
            playbackSettings.VideoBitrate,
            playbackSettings.VideoBufferSize,
            playbackSettings.VideoTrackTimeScale,
            playbackSettings.NormalizeColors,
            playbackSettings.Deinterlace);

        // only use graphics engine when we have elements, and are normalizing video
        if (videoFormat != VideoFormat.Copy && (graphicsElementContexts.Count > 0 || graphicsElements.Count > 0))
        {
            FrameSize targetSize = await desiredState.CroppedSize.IfNoneAsync(desiredState.ScaledSize);

            FrameRate frameRate = await playbackSettings.FrameRate
                .IfNoneAsync(new FrameRate(videoVersion.MediaVersion.RFrameRate));

            var context = new GraphicsEngineContext(
                channel.Number,
                audioVersion.MediaItem,
                graphicsElementContexts,
                TemplateVariables: [],
                new Resolution { Width = targetSize.Width, Height = targetSize.Height },
                channel.FFmpegProfile.Resolution,
                frameRate,
                channelStartTime,
                start,
                now > start ? now - start : TimeSpan.Zero,
                finish - now,
                originalContentDuration);

            context = await _graphicsElementLoader.LoadAll(context, graphicsElements, cancellationToken);

            if (context?.Elements?.Count > 0)
            {
                graphicsEngineInput = new GraphicsEngineInput();
                graphicsEngineContext = context;
            }
        }

        var ffmpegState = new FFmpegState(
            saveReports,
            hwAccel,
            hwAccel,
            VaapiDriverName(hwAccel, vaapiDriver),
            VaapiDeviceName(hwAccel, vaapiDevice),
            playbackSettings.StreamSeek,
            finish - now,
            channel.StreamingMode != StreamingMode.HttpLiveStreamingDirect,
            "ErsatzTV",
            channel.Name,
            maybeAudioStream.Map(s => Optional(s.Language)).Flatten(),
            subtitleLanguage,
            subtitleTitle,
            outputFormat,
            hlsPlaylistPath,
            hlsSegmentTemplate,
            hlsInitTemplate,
            hlsSegmentOptions,
            ptsOffset,
            playbackSettings.ThreadCount,
            qsvExtraHardwareFrames,
            videoVersion.MediaVersion is BackgroundImageMediaVersion { IsSongWithProgress: true },
            false,
            GetTonemapAlgorithm(playbackSettings),
            channel.Number == FileSystemLayout.TranscodeTroubleshootingChannel);

        _logger.LogDebug("FFmpeg desired state {FrameState}", desiredState);

        IPipelineBuilder pipelineBuilder = await _pipelineBuilderFactory.GetBuilder(
            hwAccel,
            videoInputFile,
            audioInputFile,
            watermarkInputFile,
            subtitleInputFile,
            Option<ConcatInputFile>.None,
            graphicsEngineInput,
            VaapiDisplayName(hwAccel, vaapiDisplay),
            VaapiDriverName(hwAccel, vaapiDriver),
            VaapiDeviceName(hwAccel, vaapiDevice),
            await customReportsFolder.IfNoneAsync(FileSystemLayout.FFmpegReportsFolder),
            FileSystemLayout.FontsCacheFolder,
            ffmpegPath,
            cancellationToken);

        FFmpegPipeline pipeline = pipelineBuilder.Build(ffmpegState, desiredState);

        pipelineAction?.Invoke(pipeline);

        Command command = GetCommand(
            ffmpegPath,
            videoInputFile,
            audioInputFile,
            watermarkInputFile,
            Option<ConcatInputFile>.None,
            graphicsEngineInput,
            pipeline);

        return new PlayoutItemResult(command, graphicsEngineContext, videoVersion.MediaItem.Id);
    }

    private async Task<ScanKind> ProbeScanKind(
        string ffmpegPath,
        MediaItem mediaItem,
        CancellationToken cancellationToken)
    {
        var headVersion = mediaItem.GetHeadVersion();
        if (headVersion.VideoScanKind is VideoScanKind.Interlaced)
        {
            _logger.LogDebug("Container is marked {ScanKind}", headVersion.VideoScanKind);
            return ScanKind.Interlaced;
        }

        // skip probe if disabled
        if (!await _configElementRepository.GetValue<bool>(
                ConfigElementKey.FFmpegProbeForInterlacedFrames,
                cancellationToken).IfNoneAsync(false))
        {
            _logger.LogDebug("Probe for interlaced frames is disabled");
            return ScanKind.Progressive;
        }

        if (headVersion.InterlacedRatio is null)
        {
            _logger.LogDebug("Will probe for interlaced frames");

            Option<double> maybeInterlacedRatio =
                await _localStatisticsProvider.GetInterlacedRatio(ffmpegPath, mediaItem, cancellationToken);
            foreach (double ratio in maybeInterlacedRatio)
            {
                await _mediaItemRepository.SetInterlacedRatio(mediaItem, ratio);
            }
        }

        var result = headVersion.InterlacedRatio > 0.05 ? ScanKind.Interlaced : ScanKind.Progressive;
        _logger.LogDebug(
            "Content has interlaced ratio of {Ratio} - will consider as {ScanKind}",
            headVersion.InterlacedRatio,
            result);
        return result;
    }

    private async Task<bool> ProbeHasMultipleProfiles(
        string ffmpegPath,
        MediaItem mediaItem,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Will probe for h264 profile count");

        Option<int> profileCount =
            await _localStatisticsProvider.GetProfileCount(ffmpegPath, mediaItem, cancellationToken);

        return await profileCount.IfNoneAsync(1) > 1;
    }

    public async Task<Command> ForError(
        string ffmpegPath,
        Channel channel,
        DateTimeOffset now,
        Option<TimeSpan> duration,
        string errorMessage,
        bool hlsRealtime,
        TimeSpan ptsOffset,
        string vaapiDisplay,
        VaapiDriver vaapiDriver,
        string vaapiDevice,
        Option<int> qsvExtraHardwareFrames,
        CancellationToken cancellationToken)
    {
        FFmpegPlaybackSettings playbackSettings = FFmpegPlaybackSettingsCalculator.CalculateGeneratedImageSettings(
            channel.StreamingMode,
            channel.FFmpegProfile,
            hlsRealtime);

        Resolution desiredResolution = channel.FFmpegProfile.Resolution;

        var fontSize = (int)Math.Round(channel.FFmpegProfile.Resolution.Height / 20.0);
        var margin = (int)Math.Round(channel.FFmpegProfile.Resolution.Height * 0.05);

        string subtitleFile = await new SubtitleBuilder(_tempFilePool)
            .WithResolution(desiredResolution)
            .WithFontName("Roboto")
            .WithFontSize(fontSize)
            .WithAlignment(2)
            .WithMarginV(margin)
            .WithPrimaryColor("&HFFFFFF")
            .WithFormattedContent(errorMessage.Replace(Environment.NewLine, "\\N"))
            .BuildFile();

        string audioFormat = playbackSettings.AudioFormat switch
        {
            FFmpegProfileAudioFormat.Ac3 => AudioFormat.Ac3,
            FFmpegProfileAudioFormat.AacLatm => AudioFormat.AacLatm,
            _ => AudioFormat.Aac
        };

        var audioState = new AudioState(
            audioFormat,
            playbackSettings.AudioChannels,
            playbackSettings.AudioBitrate,
            playbackSettings.AudioBufferSize,
            playbackSettings.AudioSampleRate,
            false,
            AudioFilter.None,
            playbackSettings.TargetLoudness);

        string videoFormat = GetVideoFormat(playbackSettings);

        var desiredState = new FrameState(
            playbackSettings.RealtimeOutput,
            InfiniteLoop: false,
            videoFormat,
            GetVideoProfile(videoFormat, channel.FFmpegProfile.VideoProfile),
            VideoPreset.Unset,
            channel.FFmpegProfile.AllowBFrames,
            new PixelFormatYuv420P(),
            new FrameSize(desiredResolution.Width, desiredResolution.Height),
            new FrameSize(desiredResolution.Width, desiredResolution.Height),
            Option<FrameSize>.None,
            channel.FFmpegProfile.PadMode is FilterMode.HardwareIfPossible
                ? FFmpegFilterMode.HardwareIfPossible
                : FFmpegFilterMode.Software,
            IsAnamorphic: false,
            playbackSettings.FrameRate,
            playbackSettings.VideoBitrate,
            playbackSettings.VideoBufferSize,
            playbackSettings.VideoTrackTimeScale,
            playbackSettings.NormalizeColors,
            playbackSettings.Deinterlace);

        string videoPath = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ResourcesCacheFolder,
            "background.png");

        var videoVersion = BackgroundImageMediaVersion.ForPath(videoPath, desiredResolution);

        var ffmpegVideoStream = new VideoStream(
            0,
            VideoFormat.GeneratedImage,
            string.Empty,
            new PixelFormatUnknown(), // leave this unknown so we convert to desired yuv420p
            ColorParams.Default,
            new FrameSize(videoVersion.Width, videoVersion.Height),
            videoVersion.SampleAspectRatio,
            videoVersion.DisplayAspectRatio,
            None,
            true,
            ScanKind.Progressive);

        var videoInputFile = new VideoInputFile(videoPath, new List<VideoStream> { ffmpegVideoStream });

        // TODO: ignore accel if this already failed once
        HardwareAccelerationMode hwAccel = GetHardwareAccelerationMode(playbackSettings);
        _logger.LogDebug("HW accel mode: {HwAccel}", hwAccel);

        var hlsOptions = GetHlsOptions(channel, now, ptsOffset, audioFormat);

        var ffmpegState = new FFmpegState(
            channel.Number == FileSystemLayout.TranscodeTroubleshootingChannel,
            HardwareAccelerationMode.None, // no hw accel decode since errors loop
            hwAccel,
            VaapiDriverName(hwAccel, vaapiDriver),
            VaapiDeviceName(hwAccel, vaapiDevice),
            playbackSettings.StreamSeek,
            duration,
            channel.StreamingMode != StreamingMode.HttpLiveStreamingDirect,
            "ErsatzTV",
            channel.Name,
            None,
            None,
            None,
            hlsOptions.OutputFormat,
            hlsOptions.HlsPlaylistPath,
            hlsOptions.HlsSegmentTemplate,
            hlsOptions.HlsInitTemplate,
            hlsOptions.HlsSegmentOptions,
            ptsOffset,
            Option<int>.None,
            qsvExtraHardwareFrames,
            false,
            false,
            GetTonemapAlgorithm(playbackSettings),
            channel.Number == FileSystemLayout.TranscodeTroubleshootingChannel);

        var ffmpegSubtitleStream = new ErsatzTV.FFmpeg.MediaStream(0, "ass", StreamKind.Video);

        var audioInputFile = new NullAudioInputFile(audioState);

        var subtitleInputFile = new SubtitleInputFile(
            subtitleFile,
            new List<ErsatzTV.FFmpeg.MediaStream> { ffmpegSubtitleStream },
            SubtitleMethod.Burn);

        _logger.LogDebug("FFmpeg desired error state {FrameState}", desiredState);

        IPipelineBuilder pipelineBuilder = await _pipelineBuilderFactory.GetBuilder(
            hwAccel,
            videoInputFile,
            audioInputFile,
            Option<WatermarkInputFile>.None,
            subtitleInputFile,
            Option<ConcatInputFile>.None,
            Option<GraphicsEngineInput>.None,
            VaapiDisplayName(hwAccel, vaapiDisplay),
            VaapiDriverName(hwAccel, vaapiDriver),
            VaapiDeviceName(hwAccel, vaapiDevice),
            channel.Number == FileSystemLayout.TranscodeTroubleshootingChannel
                ? FileSystemLayout.TranscodeTroubleshootingFolder
                : FileSystemLayout.FFmpegReportsFolder,
            FileSystemLayout.FontsCacheFolder,
            ffmpegPath,
            cancellationToken);

        FFmpegPipeline pipeline = pipelineBuilder.Build(ffmpegState, desiredState);

        return GetCommand(ffmpegPath, videoInputFile, audioInputFile, None, None, None, pipeline);
    }

    public async Task<Command> Slug(
        string ffmpegPath,
        Channel channel,
        DateTimeOffset now,
        TimeSpan duration,
        bool hlsRealtime,
        TimeSpan ptsOffset,
        CancellationToken cancellationToken)
    {
        FFmpegPlaybackSettings playbackSettings = FFmpegPlaybackSettingsCalculator.CalculateGeneratedImageSettings(
            channel.StreamingMode,
            channel.FFmpegProfile,
            hlsRealtime);

        Resolution desiredResolution = channel.FFmpegProfile.Resolution;

        string audioFormat = playbackSettings.AudioFormat switch
        {
            FFmpegProfileAudioFormat.Ac3 => AudioFormat.Ac3,
            FFmpegProfileAudioFormat.AacLatm => AudioFormat.AacLatm,
            _ => AudioFormat.Aac
        };

        var audioState = new AudioState(
            audioFormat,
            playbackSettings.AudioChannels,
            playbackSettings.AudioBitrate,
            playbackSettings.AudioBufferSize,
            playbackSettings.AudioSampleRate,
            false,
            AudioFilter.None,
            playbackSettings.TargetLoudness);

        string videoFormat = GetVideoFormat(playbackSettings);

        var desiredState = new FrameState(
            playbackSettings.RealtimeOutput,
            InfiniteLoop: false,
            videoFormat,
            GetVideoProfile(videoFormat, channel.FFmpegProfile.VideoProfile),
            VideoPreset.Unset,
            channel.FFmpegProfile.AllowBFrames,
            new PixelFormatYuv420P(),
            new FrameSize(desiredResolution.Width, desiredResolution.Height),
            new FrameSize(desiredResolution.Width, desiredResolution.Height),
            Option<FrameSize>.None,
            channel.FFmpegProfile.PadMode is FilterMode.HardwareIfPossible
                ? FFmpegFilterMode.HardwareIfPossible
                : FFmpegFilterMode.Software,
            IsAnamorphic: false,
            Option<FrameRate>.None,
            playbackSettings.VideoBitrate,
            playbackSettings.VideoBufferSize,
            playbackSettings.VideoTrackTimeScale,
            playbackSettings.NormalizeColors,
            playbackSettings.Deinterlace);

        var frameRate = await playbackSettings.FrameRate.IfNoneAsync(new FrameRate("24"));

        var ffmpegVideoStream = new VideoStream(
            0,
            VideoFormat.GeneratedImage,
            string.Empty,
            new PixelFormatUnknown(), // leave this unknown so we convert to desired yuv420p
            ColorParams.Default,
            desiredState.PaddedSize,
            MaybeSampleAspectRatio: "1:1",
            DisplayAspectRatio: string.Empty,
            frameRate,
            true,
            ScanKind.Progressive);

        var videoInputFile = new LavfiInputFile(
            $"color=c=black:s={desiredState.PaddedSize.Width}x{desiredState.PaddedSize.Height}:r={frameRate.FrameRateString}:d={duration.TotalSeconds}",
            ffmpegVideoStream);

        HardwareAccelerationMode hwAccel = GetHardwareAccelerationMode(playbackSettings);

        var hlsOptions = GetHlsOptions(channel, now, ptsOffset, audioFormat);

        var ffmpegState = new FFmpegState(
            channel.Number == FileSystemLayout.TranscodeTroubleshootingChannel,
            HardwareAccelerationMode.None, // no hw accel decode since errors loop
            hwAccel,
            VaapiDriverName(hwAccel, channel.FFmpegProfile.VaapiDriver),
            VaapiDeviceName(hwAccel, channel.FFmpegProfile.VaapiDevice),
            playbackSettings.StreamSeek,
            duration,
            channel.StreamingMode != StreamingMode.HttpLiveStreamingDirect,
            "ErsatzTV",
            channel.Name,
            None,
            None,
            None,
            hlsOptions.OutputFormat,
            hlsOptions.HlsPlaylistPath,
            hlsOptions.HlsSegmentTemplate,
            hlsOptions.HlsInitTemplate,
            hlsOptions.HlsSegmentOptions,
            ptsOffset,
            Option<int>.None,
            Optional(channel.FFmpegProfile.QsvExtraHardwareFrames),
            false,
            false,
            GetTonemapAlgorithm(playbackSettings),
            channel.Number == FileSystemLayout.TranscodeTroubleshootingChannel);

        var audioInputFile = new NullAudioInputFile(audioState);

        IPipelineBuilder pipelineBuilder = await _pipelineBuilderFactory.GetBuilder(
            hwAccel,
            videoInputFile,
            audioInputFile,
            Option<WatermarkInputFile>.None,
            Option<SubtitleInputFile>.None,
            Option<ConcatInputFile>.None,
            Option<GraphicsEngineInput>.None,
            VaapiDisplayName(hwAccel, channel.FFmpegProfile.VaapiDisplay),
            VaapiDriverName(hwAccel, channel.FFmpegProfile.VaapiDriver),
            VaapiDeviceName(hwAccel, channel.FFmpegProfile.VaapiDevice),
            channel.Number == FileSystemLayout.TranscodeTroubleshootingChannel
                ? FileSystemLayout.TranscodeTroubleshootingFolder
                : FileSystemLayout.FFmpegReportsFolder,
            FileSystemLayout.FontsCacheFolder,
            ffmpegPath,
            cancellationToken);

        FFmpegPipeline pipeline = pipelineBuilder.Build(ffmpegState, desiredState);

        return GetCommand(ffmpegPath, videoInputFile, audioInputFile, None, None, None, pipeline);
    }

    public async Task<Command> ConcatChannel(
        string ffmpegPath,
        bool saveReports,
        Channel channel,
        string scheme,
        string host,
        CancellationToken cancellationToken)
    {
        var resolution = new FrameSize(channel.FFmpegProfile.Resolution.Width, channel.FFmpegProfile.Resolution.Height);

        var concatInputFile = new ConcatInputFile(
            $"http://localhost:{Settings.StreamingPort}/ffmpeg/concat/{channel.Number}?mode=ts-legacy",
            resolution);

        IPipelineBuilder pipelineBuilder = await _pipelineBuilderFactory.GetBuilder(
            HardwareAccelerationMode.None,
            Option<VideoInputFile>.None,
            Option<AudioInputFile>.None,
            Option<WatermarkInputFile>.None,
            Option<SubtitleInputFile>.None,
            concatInputFile,
            Option<GraphicsEngineInput>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            FileSystemLayout.FFmpegReportsFolder,
            FileSystemLayout.FontsCacheFolder,
            ffmpegPath,
            cancellationToken);

        FFmpegPipeline pipeline = pipelineBuilder.Concat(
            concatInputFile,
            FFmpegState.Concat(saveReports, channel.Name));

        return GetCommand(ffmpegPath, None, None, None, concatInputFile, None, pipeline);
    }

    public async Task<Command> WrapSegmenter(
        string ffmpegPath,
        bool saveReports,
        Channel channel,
        string scheme,
        string host,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var resolution = new FrameSize(channel.FFmpegProfile.Resolution.Width, channel.FFmpegProfile.Resolution.Height);

        string accessTokenQuery = string.IsNullOrWhiteSpace(accessToken)
            ? string.Empty
            : $"&access_token={accessToken}";

        var concatInputFile = new ConcatInputFile(
            $"http://localhost:{Settings.StreamingPort}/iptv/channel/{channel.Number}.m3u8?mode=segmenter{accessTokenQuery}",
            resolution);

        if (channel.FFmpegProfile.AudioFormat is FFmpegProfileAudioFormat.AacLatm)
        {
            concatInputFile.AudioFormat = AudioFormat.AacLatm;
        }

        // TODO: save reports?
        string defaultScript = await _configElementRepository
            .GetValue<string>(ConfigElementKey.FFmpegDefaultMpegTsScript, cancellationToken)
            .IfNoneAsync("default");
        List<MpegTsScript> allScripts = _mpegTsScriptService.GetScripts();
        Option<MpegTsScript> maybeScript = Optional(allScripts.Find(s => string.Equals(s.Id, defaultScript, StringComparison.OrdinalIgnoreCase)));
        foreach (var script in maybeScript)
        {
            Option<Command> maybeCommand = await _mpegTsScriptService.Execute(
                script,
                channel,
                concatInputFile.Url,
                ffmpegPath);
            foreach (var command in maybeCommand)
            {
                return command;
            }
        }

        if (maybeScript.IsNone)
        {
            _logger.LogWarning("Unable to locate MPEG-TS Script in folder {Id}", defaultScript);
        }

        IPipelineBuilder pipelineBuilder = await _pipelineBuilderFactory.GetBuilder(
            HardwareAccelerationMode.None,
            Option<VideoInputFile>.None,
            Option<AudioInputFile>.None,
            Option<WatermarkInputFile>.None,
            Option<SubtitleInputFile>.None,
            concatInputFile,
            Option<GraphicsEngineInput>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            FileSystemLayout.FFmpegReportsFolder,
            FileSystemLayout.FontsCacheFolder,
            ffmpegPath,
            cancellationToken);

        FFmpegPipeline pipeline = pipelineBuilder.WrapSegmenter(
            concatInputFile,
            FFmpegState.Concat(saveReports, channel.Name));

        return GetCommand(ffmpegPath, None, None, None, concatInputFile, None, pipeline);
    }

    public async Task<Command> ResizeImage(
        string ffmpegPath,
        string inputFile,
        string outputFile,
        int height,
        CancellationToken cancellationToken)
    {
        var videoInputFile = new VideoInputFile(
            inputFile,
            new List<VideoStream>
            {
                new(
                    0,
                    string.Empty,
                    string.Empty,
                    None,
                    ColorParams.Default,
                    FrameSize.Unknown,
                    string.Empty,
                    string.Empty,
                    None,
                    true,
                    ScanKind.Progressive)
            });

        IPipelineBuilder pipelineBuilder = await _pipelineBuilderFactory.GetBuilder(
            HardwareAccelerationMode.None,
            videoInputFile,
            Option<AudioInputFile>.None,
            Option<WatermarkInputFile>.None,
            Option<SubtitleInputFile>.None,
            Option<ConcatInputFile>.None,
            Option<GraphicsEngineInput>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            FileSystemLayout.FFmpegReportsFolder,
            FileSystemLayout.FontsCacheFolder,
            ffmpegPath,
            cancellationToken);

        FFmpegPipeline pipeline = pipelineBuilder.Resize(outputFile, new FrameSize(-1, height));

        return GetCommand(ffmpegPath, videoInputFile, None, None, None, None, pipeline, false);
    }

    public Task<Either<BaseError, string>> GenerateSongImage(
        string ffmpegPath,
        string ffprobePath,
        Option<string> subtitleFile,
        Channel channel,
        MediaVersion videoVersion,
        string videoPath,
        bool boxBlur,
        Option<string> watermarkPath,
        WatermarkLocation watermarkLocation,
        int horizontalMarginPercent,
        int verticalMarginPercent,
        int watermarkWidthPercent,
        CancellationToken cancellationToken) =>
        _ffmpegProcessService.GenerateSongImage(
            ffmpegPath,
            ffprobePath,
            subtitleFile,
            channel,
            videoVersion,
            videoPath,
            boxBlur,
            watermarkPath,
            watermarkLocation,
            horizontalMarginPercent,
            verticalMarginPercent,
            watermarkWidthPercent,
            cancellationToken);

    public async Task<Command> SeekTextSubtitle(
        string ffmpegPath,
        string inputFile,
        string codec,
        TimeSpan seek,
        CancellationToken cancellationToken)
    {
        var videoInputFile = new VideoInputFile(
            inputFile,
            new List<VideoStream>
            {
                new(
                    0,
                    codec,
                    string.Empty,
                    None,
                    ColorParams.Default,
                    FrameSize.Unknown,
                    string.Empty,
                    string.Empty,
                    None,
                    true,
                    ScanKind.Progressive)
            });

        IPipelineBuilder pipelineBuilder = await _pipelineBuilderFactory.GetBuilder(
            HardwareAccelerationMode.None,
            videoInputFile,
            Option<AudioInputFile>.None,
            Option<WatermarkInputFile>.None,
            Option<SubtitleInputFile>.None,
            Option<ConcatInputFile>.None,
            Option<GraphicsEngineInput>.None,
            Option<string>.None,
            Option<string>.None,
            Option<string>.None,
            FileSystemLayout.FFmpegReportsFolder,
            FileSystemLayout.FontsCacheFolder,
            ffmpegPath,
            cancellationToken);

        FFmpegPipeline pipeline = pipelineBuilder.Seek(inputFile, codec, seek);

        return GetCommand(ffmpegPath, videoInputFile, None, None, None, None, pipeline, false);
    }

    private Command GetCommand(
        string ffmpegPath,
        Option<VideoInputFile> videoInputFile,
        Option<AudioInputFile> audioInputFile,
        Option<WatermarkInputFile> watermarkInputFile,
        Option<ConcatInputFile> concatInputFile,
        Option<GraphicsEngineInput> graphicsEngineInput,
        FFmpegPipeline pipeline,
        bool log = true)
    {
        IEnumerable<string> loggedSteps = pipeline.PipelineSteps.Map(ps => ps.GetType().Name);
        IEnumerable<string> loggedAudioFilters =
            audioInputFile.Map(f => f.FilterSteps.Map(af => af.GetType().Name)).Flatten();
        IEnumerable<string> loggedVideoFilters =
            videoInputFile.Map(f => f.FilterSteps.Map(vf => vf.GetType().Name)).Flatten();

        if (log)
        {
            _logger.LogDebug(
                "FFmpeg pipeline {PipelineSteps}, {AudioFilters}, {VideoFilters}",
                loggedSteps,
                loggedAudioFilters,
                loggedVideoFilters
            );
        }

        IList<EnvironmentVariable> environmentVariables =
            CommandGenerator.GenerateEnvironmentVariables(pipeline.PipelineSteps);
        IList<string> arguments = CommandGenerator.GenerateArguments(
            videoInputFile,
            audioInputFile,
            watermarkInputFile,
            concatInputFile,
            graphicsEngineInput,
            pipeline.PipelineSteps,
            pipeline.IsIntelVaapiOrQsv);

        if (environmentVariables.Any())
        {
            _logger.LogDebug("FFmpeg environment variables {EnvVars}", environmentVariables);
        }

        return Cli.Wrap(ffmpegPath)
            .WithArguments(arguments)
            .WithValidation(CommandResultValidation.None)
            .WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null))
            .WithEnvironmentVariables(environmentVariables.ToDictionary(e => e.Key, e => e.Value));
    }

    private static Option<string> VaapiDisplayName(HardwareAccelerationMode accelerationMode, string vaapiDisplay) =>
        accelerationMode == HardwareAccelerationMode.Vaapi ? vaapiDisplay : Option<string>.None;

    private static Option<string> VaapiDriverName(HardwareAccelerationMode accelerationMode, VaapiDriver driver)
    {
        if (accelerationMode == HardwareAccelerationMode.Vaapi)
        {
            switch (driver)
            {
                case VaapiDriver.i965:
                    return "i965";
                case VaapiDriver.iHD:
                    return "iHD";
                case VaapiDriver.RadeonSI:
                    return "radeonsi";
                case VaapiDriver.Nouveau:
                    return "nouveau";
            }
        }

        return Option<string>.None;
    }

    private static Option<string> VaapiDeviceName(HardwareAccelerationMode accelerationMode, string vaapiDevice) =>
        accelerationMode == HardwareAccelerationMode.Vaapi ||
        OperatingSystem.IsLinux() && accelerationMode == HardwareAccelerationMode.Qsv
            ? string.IsNullOrWhiteSpace(vaapiDevice) ? "/dev/dri/renderD128" : vaapiDevice
            : Option<string>.None;

    private static string GetVideoFormat(FFmpegPlaybackSettings playbackSettings) =>
        playbackSettings.VideoFormat switch
        {
            FFmpegProfileVideoFormat.Av1 => VideoFormat.Av1,
            FFmpegProfileVideoFormat.Hevc => VideoFormat.Hevc,
            FFmpegProfileVideoFormat.H264 => VideoFormat.H264,
            FFmpegProfileVideoFormat.Mpeg2Video => VideoFormat.Mpeg2Video,
            FFmpegProfileVideoFormat.Copy => VideoFormat.Copy,
            _ => throw new ArgumentOutOfRangeException($"unexpected video format {playbackSettings.VideoFormat}")
        };

    private static string GetTonemapAlgorithm(FFmpegPlaybackSettings playbackSettings) =>
        playbackSettings.TonemapAlgorithm switch
        {
            FFmpegProfileTonemapAlgorithm.Linear => TonemapAlgorithm.Linear,
            FFmpegProfileTonemapAlgorithm.Clip => TonemapAlgorithm.Clip,
            FFmpegProfileTonemapAlgorithm.Gamma => TonemapAlgorithm.Gamma,
            FFmpegProfileTonemapAlgorithm.Reinhard => TonemapAlgorithm.Reinhard,
            FFmpegProfileTonemapAlgorithm.Mobius => TonemapAlgorithm.Mobius,
            FFmpegProfileTonemapAlgorithm.Hable => TonemapAlgorithm.Hable,
            _ => throw new ArgumentOutOfRangeException(
                $"unexpected tonemap algorithm {playbackSettings.TonemapAlgorithm}")
        };

    private static Option<string> GetVideoProfile(string videoFormat, string videoProfile) =>
        (videoFormat, (videoProfile ?? string.Empty).ToLowerInvariant()) switch
        {
            (VideoFormat.H264, VideoProfile.Main) => VideoProfile.Main,
            (VideoFormat.H264, VideoProfile.High) => VideoProfile.High,
            (VideoFormat.H264, VideoProfile.High10) => VideoProfile.High10,
            (VideoFormat.H264, VideoProfile.High444p) => VideoProfile.High444p,
            _ => Option<string>.None
        };

    private static Option<string> GetVideoPreset(
        HardwareAccelerationMode hardwareAccelerationMode,
        string videoFormat,
        string videoPreset,
        int bitDepth) =>
        AvailablePresets
            .ForAccelAndFormat(hardwareAccelerationMode, videoFormat, bitDepth)
            .Find(p => string.Equals(p, videoPreset, StringComparison.OrdinalIgnoreCase));

    private static HardwareAccelerationMode GetHardwareAccelerationMode(FFmpegPlaybackSettings playbackSettings) =>
        playbackSettings.HardwareAcceleration switch
        {
            //_ when fillerKind == FillerKind.Fallback => HardwareAccelerationMode.None,
            HardwareAccelerationKind.Nvenc => HardwareAccelerationMode.Nvenc,
            HardwareAccelerationKind.Qsv => HardwareAccelerationMode.Qsv,
            HardwareAccelerationKind.Vaapi => HardwareAccelerationMode.Vaapi,
            HardwareAccelerationKind.VideoToolbox => HardwareAccelerationMode.VideoToolbox,
            HardwareAccelerationKind.Amf => HardwareAccelerationMode.Amf,
            HardwareAccelerationKind.V4l2m2m => HardwareAccelerationMode.V4l2m2m,
            HardwareAccelerationKind.Rkmpp => HardwareAccelerationMode.Rkmpp,
            _ => HardwareAccelerationMode.None
        };

    private async Task<bool> IsWatermarkAnimated(string ffprobePath, string path)
    {
        try
        {
            var cacheKey = $"image.animated.{Path.GetFileName(path)}";
            if (_memoryCache.TryGetValue(cacheKey, out bool animated))
            {
                return animated;
            }

            BufferedCommandResult result = await Cli.Wrap(ffprobePath)
                .WithArguments(
                [
                    "-loglevel", "error",
                    "-select_streams", "v:0",
                    "-count_frames",
                    "-show_entries", "stream=nb_read_frames",
                    "-print_format", "csv",
                    path
                ])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(Encoding.UTF8);

            if (result.ExitCode == 0)
            {
                string output = result.StandardOutput;
                output = output.Replace("stream,", string.Empty);
                if (int.TryParse(output, out int frameCount))
                {
                    bool isAnimated = frameCount > 1;
                    _memoryCache.Set(cacheKey, isAnimated, TimeSpan.FromDays(1));
                    return isAnimated;
                }
            }
            else
            {
                _logger.LogWarning(
                    "Error checking frame count for file {File} exit code {ExitCode}",
                    path,
                    result.ExitCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking frame count for file {File}", path);
        }

        return false;
    }

    private static HlsOptions GetHlsOptions(Channel channel, DateTimeOffset now, TimeSpan ptsOffset, string audioFormat)
    {
        OutputFormatKind outputFormat = OutputFormatKind.MpegTs;
        switch (channel.StreamingMode)
        {
            case StreamingMode.HttpLiveStreamingSegmenter:
                outputFormat = OutputFormatKind.Hls;
                break;
        }

        Option<string> hlsPlaylistPath = outputFormat is OutputFormatKind.Hls or OutputFormatKind.HlsMp4
            ? Path.Combine(FileSystemLayout.TranscodeFolder, channel.Number, "live.m3u8")
            : Option<string>.None;

        long nowSeconds = now.ToUnixTimeSeconds();

        Option<string> hlsSegmentTemplate = outputFormat switch
        {
            OutputFormatKind.Hls => Path.Combine(FileSystemLayout.TranscodeFolder, channel.Number, "live%06d.ts"),
            OutputFormatKind.HlsMp4 => Path.Combine(
                FileSystemLayout.TranscodeFolder,
                channel.Number,
                $"live_{nowSeconds}_%06d.m4s"),
            _ => Option<string>.None
        };

        Option<string> hlsInitTemplate = outputFormat switch
        {
            OutputFormatKind.HlsMp4 => $"{nowSeconds}_init.mp4",
            _ => Option<string>.None
        };

        Option<string> hlsSegmentOptions = Option<string>.None;
        if (outputFormat is OutputFormatKind.Hls)
        {
            string options = string.Empty;

            if (ptsOffset == TimeSpan.Zero)
            {
                options += "+initial_discontinuity";
            }

            if (audioFormat == AudioFormat.AacLatm)
            {
                options += "+latm";
            }

            if (!string.IsNullOrWhiteSpace(options))
            {
                hlsSegmentOptions = $"mpegts_flags={options}";
            }
        }

        return new HlsOptions(outputFormat, hlsPlaylistPath, hlsSegmentTemplate, hlsInitTemplate, hlsSegmentOptions);
    }

    private sealed record HlsOptions(
        OutputFormatKind OutputFormat,
        Option<string> HlsPlaylistPath,
        Option<string> HlsSegmentTemplate,
        Option<string> HlsInitTemplate,
        Option<string> HlsSegmentOptions);
}

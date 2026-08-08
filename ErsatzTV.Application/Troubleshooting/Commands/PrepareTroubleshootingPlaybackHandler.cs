using System.Globalization;
using System.IO.Abstractions;
using CliWrap;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Next.Config;
using ErsatzTV.Core.Notifications;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.State;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Serilog.Events;
using Subtitle = ErsatzTV.Core.Domain.Subtitle;

namespace ErsatzTV.Application.Troubleshooting;

public class PrepareTroubleshootingPlaybackHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IPlexPathReplacementService plexPathReplacementService,
    IJellyfinPathReplacementService jellyfinPathReplacementService,
    IEmbyPathReplacementService embyPathReplacementService,
    IFFmpegProcessService ffmpegProcessService,
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem,
    ISongVideoGenerator songVideoGenerator,
    IWatermarkSelector watermarkSelector,
    IEntityLocker entityLocker,
    IChannelConfigConverter channelConfigConverter,
    IPlayoutItemConverter playoutItemConverter,
    IMediator mediator,
    LoggingLevelSwitches loggingLevelSwitches,
    ILogger<PrepareTroubleshootingPlaybackHandler> logger)
    : TroubleshootingHandlerBase(
        plexPathReplacementService,
        jellyfinPathReplacementService,
        embyPathReplacementService,
        fileSystem: fileSystem), IRequestHandler<PrepareTroubleshootingPlayback, Either<BaseError, PlayoutItemResult>>
{
    private readonly IFileSystem _fileSystem = fileSystem;

    private const ChannelSubtitleMode SubtitleMode = ChannelSubtitleMode.Any;

    public async Task<Either<BaseError, PlayoutItemResult>> Handle(
        PrepareTroubleshootingPlayback request,
        CancellationToken cancellationToken)
    {
        var currentStreamingLevel = loggingLevelSwitches.StreamingLevelSwitch.MinimumLevel;
        loggingLevelSwitches.StreamingLevelSwitch.MinimumLevel = LogEventLevel.Debug;

        try
        {
            using var logContext = LogContext.PushProperty(InMemoryLogService.CorrelationIdKey, request.SessionId);
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            if (request.ChannelId > 0)
            {
                if (request.Start.IsNone)
                {
                    return BaseError.New("Channel start is required");
                }

                if (entityLocker.IsTroubleshootingPlaybackLocked())
                {
                    return BaseError.New("Troubleshooting playback is locked");
                }

                entityLocker.LockTroubleshootingPlayback();

                localFileSystem.EnsureFolderExists(FileSystemLayout.TranscodeTroubleshootingFolder);
                localFileSystem.EmptyFolder(FileSystemLayout.TranscodeTroubleshootingFolder);

                foreach (var start in request.Start)
                {
                    Option<Channel> maybeChannel = await dbContext.Channels
                        .AsNoTracking()
                        .SelectOneAsync(c => c.Id, c => c.Id == request.ChannelId, cancellationToken);

                    foreach (var channel in maybeChannel)
                    {
                        Either<BaseError, PlayoutItemProcessModel> result = await mediator.Send(
                            new GetPlayoutItemProcessByChannelNumber(
                                channel.Number,
                                request.StreamingMode,
                                start,
                                StartAtZero: false,
                                HlsRealtime: false,
                                start,
                                TimeSpan.Zero,
                                TargetFramerate: Option<FrameRate>.None,
                                IsTroubleshooting: true,
                                request.FFmpegProfileId),
                            cancellationToken);

                        foreach (var error in result.LeftToSeq())
                        {
                            await mediator.Publish(
                                new PlaybackTroubleshootingCompletedNotification(
                                    -1,
#pragma warning disable CA2201
                                    new Exception(error.ToString()),
#pragma warning restore CA2201
                                    Option<double>.None),
                                cancellationToken);
                            entityLocker.UnlockTroubleshootingPlayback();
                        }

                        return result.Map(model => new PlayoutItemResult(
                            model.Process,
                            model.GraphicsEngineContext,
                            model.MediaItemId));
                    }

                    if (maybeChannel.IsNone)
                    {
                        entityLocker.UnlockTroubleshootingPlayback();
                        return BaseError.New($"Channel {request.ChannelId} does not exist");
                    }
                }
            }

            Validation<BaseError, Tuple<MediaItem, string, string, FFmpegProfile>> validation = await Validate(
                dbContext,
                request,
                cancellationToken);
            return await validation.Match(
                tuple => GetProcess(
                    dbContext,
                    request,
                    tuple.Item1,
                    tuple.Item2,
                    tuple.Item3,
                    tuple.Item4,
                    cancellationToken),
                error => Task.FromResult<Either<BaseError, PlayoutItemResult>>(error.Join()));
        }
        catch (Exception ex)
        {
            entityLocker.UnlockTroubleshootingPlayback();
            await mediator.Publish(
                new PlaybackTroubleshootingCompletedNotification(-1, ex, Option<double>.None),
                cancellationToken);
            logger.LogError(ex, "Error while preparing troubleshooting playback");
            return BaseError.New(ex.Message);
        }
        finally
        {
            loggingLevelSwitches.StreamingLevelSwitch.MinimumLevel = currentStreamingLevel;
        }
    }

    private async Task<Either<BaseError, PlayoutItemResult>> GetProcess(
        TvContext dbContext,
        PrepareTroubleshootingPlayback request,
        MediaItem mediaItem,
        string ffmpegPath,
        string ffprobePath,
        FFmpegProfile ffmpegProfile,
        CancellationToken cancellationToken)
    {
        if (entityLocker.IsTroubleshootingPlaybackLocked())
        {
            return BaseError.New("Troubleshooting playback is locked");
        }

        entityLocker.LockTroubleshootingPlayback();

        localFileSystem.EnsureFolderExists(FileSystemLayout.TranscodeTroubleshootingFolder);
        localFileSystem.EmptyFolder(FileSystemLayout.TranscodeTroubleshootingFolder);

        string mediaPath = await GetMediaItemPath(dbContext, mediaItem, cancellationToken);
        if (string.IsNullOrEmpty(mediaPath))
        {
            logger.LogWarning("Media item {MediaItemId} does not exist on disk; cannot troubleshoot.", mediaItem.Id);
            return BaseError.New("Media item does not exist on disk");
        }

        var channel = new Channel(Guid.Empty)
        {
            Artwork = [],
            Name = "ETV",
            Number = FileSystemLayout.TranscodeTroubleshootingChannel,
            FFmpegProfile = ffmpegProfile,
            StreamingEngine = request.StreamingEngine,
            StreamingMode = request.StreamingMode,
            StreamSelectorMode = ChannelStreamSelectorMode.Troubleshooting,
            SubtitleMode = SubtitleMode
            //SongVideoMode = ChannelSongVideoMode.WithProgress
        };

        if (!string.IsNullOrEmpty(request.StreamSelector))
        {
            channel.StreamSelectorMode = ChannelStreamSelectorMode.Custom;
            channel.StreamSelector = request.StreamSelector;
        }

        MediaVersion version = mediaItem.GetHeadVersion();

        var duration = TimeSpan.FromSeconds(Math.Min(version.Duration.TotalSeconds, 30));
        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromSeconds(30);
        }

        // we cannot burst live input
        bool hlsRealtime = mediaItem is RemoteStream { IsLive: true };

        TimeSpan inPoint = TimeSpan.Zero;
        TimeSpan outPoint = duration;
        if (!hlsRealtime)
        {
            foreach (int seekSeconds in request.SeekSeconds)
            {
                inPoint = TimeSpan.FromSeconds(seekSeconds);
                if (inPoint > version.Duration)
                {
                    inPoint = version.Duration - duration;
                }

                if (inPoint + duration > version.Duration)
                {
                    duration = version.Duration - inPoint;
                }

                outPoint = inPoint + duration;
            }
        }

        List<WatermarkOptions> watermarks = [];
        if (request.WatermarkIds.Count > 0)
        {
            List<ChannelWatermark> channelWatermarks = await dbContext.ChannelWatermarks
                .AsNoTracking()
                .Where(w => request.WatermarkIds.Contains(w.Id))
                .ToListAsync(cancellationToken);

            foreach (var watermark in channelWatermarks)
            {
                watermarks.AddRange(
                    watermarkSelector.GetWatermarkOptions(
                        channel,
                        watermark,
                        Option<ChannelWatermark>.None,
                        shouldLogMessages: true));
            }
        }

        switch (request.StreamingEngine)
        {
            case StreamingEngine.Next:
                return await GetNextProcess(
                    request,
                    mediaItem,
                    ffmpegProfile,
                    channel,
                    inPoint,
                    outPoint,
                    watermarks,
                    cancellationToken);
            default:
                return await GetLegacyProcess(
                    dbContext,
                    request,
                    mediaItem,
                    mediaPath,
                    ffmpegPath,
                    ffprobePath,
                    ffmpegProfile,
                    channel,
                    inPoint,
                    watermarks,
                    cancellationToken);
        }
    }

    private async Task<Either<BaseError, PlayoutItemResult>> GetNextProcess(
        PrepareTroubleshootingPlayback request,
        MediaItem mediaItem,
        FFmpegProfile ffmpegProfile,
        Channel channel,
        TimeSpan inPoint,
        TimeSpan outPoint,
        List<WatermarkOptions> watermarks,
        CancellationToken cancellationToken)
    {
        Validation<BaseError, string> channelBinaryResult = await ChannelBinaryMustExist();
        foreach (var error in channelBinaryResult.FailToSeq())
        {
            return error;
        }

        string channelBinary = channelBinaryResult.SuccessToSeq().Head();

        // ignore fractional seconds so virtual start and playout item start always match
        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.Now.ToUnixTimeSeconds());

        ChannelConfig config = await channelConfigConverter.ToNext(
            Channels.Mapper.ProjectToViewModel(channel, playoutCount: 0),
            FFmpegProfiles.Mapper.ProjectToViewModel(ffmpegProfile),
            cancellationToken);

        config.Playout.VirtualStart = start.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture);
        logger.LogInformation("Config virtual start: {Start}", config.Playout.VirtualStart);

        string workingDirectory = FileSystemLayout.TranscodeTroubleshootingFolder;
        config.Ffmpeg.ReportsFolder = workingDirectory;

        var playoutItem = new PlayoutItem
        {
            MediaItem = mediaItem,
            MediaItemId = mediaItem.Id,
            Start = start.UtcDateTime,
            Finish = start.UtcDateTime.Add(outPoint - inPoint),
            GuideStart = null,
            GuideFinish = null,
            CustomTitle = null,
            GuideGroup = 0,
            FillerKind = FillerKind.None,
            Playout = null,
            PlayoutId = 0,
            InPoint = inPoint,
            OutPoint = outPoint,
            ChapterTitle = null,
            Watermarks = [],
            DisableWatermarks = request.WatermarkIds.Count == 0,
            PreferredAudioLanguageCode = null,
            PreferredAudioTitle = null,
            PreferredSubtitleLanguageCode = null,
            SubtitleMode = SubtitleMode,
            BlockKey = null,
            CollectionKey = null,
            CollectionEtag = null,
            PlayoutItemWatermarks = [],
            GraphicsElements = [],
            PlayoutItemGraphicsElements = []
        };

        Option<Core.Next.PlayoutItem> maybeNextPlayoutItem =
            await playoutItemConverter.ToNext(
                Some(channel),
                watermarks.HeadOrNone().Map(wm => wm.Watermark),
                TimeSpan.Zero,
                playoutItem,
                await GetSubtitles(mediaItem, request),
                shouldLogMessages: true,
                cancellationToken);

        foreach (var nextPlayoutItem in maybeNextPlayoutItem)
        {
            var playout = new Core.Next.Playout
            {
                Version = "https://ersatztv.org/playout/version/0.0.2",
                Items = [nextPlayoutItem]
            };

            localFileSystem.EnsureFolderExists(FileSystemLayout.TranscodeTroubleshootingPlayoutFolder);
            localFileSystem.EmptyFolder(FileSystemLayout.TranscodeTroubleshootingPlayoutFolder);

            string fileName = _fileSystem.Path.Combine(
                FileSystemLayout.TranscodeTroubleshootingPlayoutFolder,
                $"{playoutItem.StartOffset.ToUnixTimeMilliseconds()}_{playoutItem.FinishOffset.ToUnixTimeMilliseconds()}.json");
            await _fileSystem.File.WriteAllTextAsync(fileName, Core.Next.Serialize.ToJson(playout), cancellationToken);

            config.Playout.Folder = FileSystemLayout.TranscodeTroubleshootingPlayoutFolder;

            List<string> arguments =
                ["run", "--output-folder", workingDirectory, "--number", channel.Number, "--troubleshoot", "-"];

            string defaultOverlayFile = _fileSystem.Path.Combine(
                FileSystemLayout.NextChannelConfigOverlaysFolder,
                "default.json");
            if (_fileSystem.File.Exists(defaultOverlayFile))
            {
                arguments.Add(defaultOverlayFile);
            }

            Command command = Cli.Wrap(channelBinary)
                .WithArguments(arguments)
                .WithStandardInputPipe(PipeSource.FromString(config.ToJson()));

            return new PlayoutItemResult(
                command,
                Option<GraphicsEngineContext>.None,
                Some(request.MediaItemId));
        }

        return BaseError.New("Failed to prepare troubleshooting playback using next engine");
    }

    private async Task<Either<BaseError, PlayoutItemResult>> GetLegacyProcess(
        TvContext dbContext,
        PrepareTroubleshootingPlayback request,
        MediaItem mediaItem,
        string mediaPath,
        string ffmpegPath,
        string ffprobePath,
        FFmpegProfile ffmpegProfile,
        Channel channel,
        TimeSpan inPoint,
        List<WatermarkOptions> watermarks,
        CancellationToken cancellationToken)
    {
        MediaVersion version = mediaItem.GetHeadVersion();

        string videoPath = mediaPath;
        MediaVersion videoVersion = version;

        if (mediaItem is Song song)
        {
            (videoPath, videoVersion) = await songVideoGenerator.GenerateSongVideo(
                song,
                channel,
                ffmpegPath,
                ffprobePath,
                CancellationToken.None);

            // override watermark as song_progress_overlay.png
            if (videoVersion is BackgroundImageMediaVersion { IsSongWithProgress: true })
            {
                double ratio = channel.FFmpegProfile.Resolution.Width /
                               (double)channel.FFmpegProfile.Resolution.Height;
                bool is43 = Math.Abs(ratio - 4.0 / 3.0) < 0.01;
                string image = is43 ? "song_progress_overlay_43.png" : "song_progress_overlay.png";

                var progressWatermark = new ChannelWatermark
                {
                    Mode = ChannelWatermarkMode.Permanent,
                    Size = WatermarkSize.Scaled,
                    WidthPercent = 100,
                    HorizontalMarginPercent = 0,
                    VerticalMarginPercent = 0,
                    Opacity = 100,
                    Location = WatermarkLocation.TopLeft,
                    ImageSource = ChannelWatermarkImageSource.Resource,
                    Image = image
                };

                var progressWatermarkOption = new WatermarkOptions(
                    progressWatermark,
                    Path.Combine(FileSystemLayout.ResourcesCacheFolder, progressWatermark.Image),
                    Option<int>.None);

                watermarks.Clear();
                watermarks.Add(progressWatermarkOption);
            }
        }

        DateTimeOffset now = DateTimeOffset.Now;

        var duration = TimeSpan.FromSeconds(Math.Min(version.Duration.TotalSeconds, 30));
        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromSeconds(30);
        }

        // we cannot burst live input
        bool hlsRealtime = mediaItem is RemoteStream { IsLive: true };

        List<GraphicsElement> graphicsElements = await dbContext.GraphicsElements
            .Where(ge => request.GraphicsElementIds.Contains(ge.Id))
            .ToListAsync(cancellationToken);

        PlayoutItemResult playoutItemResult = await ffmpegProcessService.ForPlayoutItem(
            ffmpegPath,
            ffprobePath,
            saveReports: true,
            channel,
            new MediaItemVideoVersion(mediaItem, videoVersion),
            new MediaItemAudioVersion(mediaItem, version),
            videoPath,
            mediaPath,
            _ => GetSubtitles(mediaItem, request),
            string.Empty,
            string.Empty,
            string.Empty,
            SubtitleMode,
            now,
            now + duration,
            now,
            duration,
            watermarks,
            graphicsElements.Map(ge => new PlayoutItemGraphicsElement { GraphicsElement = ge }).ToList(),
            ffmpegProfile.VaapiDisplay,
            ffmpegProfile.VaapiDriver,
            ffmpegProfile.VaapiDevice,
            Option<int>.None,
            hlsRealtime,
            mediaItem is RemoteStream { IsLive: true } ? StreamInputKind.Live : StreamInputKind.Vod,
            FillerKind.None,
            inPoint,
            channelStartTime: DateTimeOffset.Now,
            TimeSpan.Zero,
            Option<FrameRate>.None,
            FileSystemLayout.TranscodeTroubleshootingFolder,
            _ => { },
            canProxy: true,
            cancellationToken);

        return playoutItemResult;
    }

    private static async Task<List<Subtitle>> GetSubtitles(MediaItem mediaItem, PrepareTroubleshootingPlayback request)
    {
        List<Subtitle> allSubtitles = mediaItem switch
        {
            Episode episode => await Optional(episode.EpisodeMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            Movie movie => await Optional(movie.MovieMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            OtherVideo otherVideo => await Optional(otherVideo.OtherVideoMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            _ => []
        };

        bool isMediaServer = mediaItem is PlexMovie or PlexEpisode or
            JellyfinMovie or JellyfinEpisode or EmbyMovie or EmbyEpisode;

        if (isMediaServer)
        {
            // closed captions are currently unsupported
            allSubtitles.RemoveAll(s => s.Codec == "eia_608");
        }

        if (request.SubtitleId is not null)
        {
            allSubtitles.RemoveAll(s => s.Id != request.SubtitleId.Value);

            foreach (Subtitle subtitle in allSubtitles)
            {
                // pretend subtitle is forced
                subtitle.Forced = true;
                return [subtitle];
            }
        }
        else if (string.IsNullOrWhiteSpace(request.StreamSelector))
        {
            allSubtitles.Clear();
        }

        return allSubtitles;
    }

    private static async Task<Validation<BaseError, Tuple<MediaItem, string, string, FFmpegProfile>>> Validate(
        TvContext dbContext,
        PrepareTroubleshootingPlayback request,
        CancellationToken cancellationToken) =>
        (await MediaItemMustExist(dbContext, request.MediaItemId, cancellationToken),
            await FFmpegPathMustExist(dbContext, cancellationToken),
            await FFprobePathMustExist(dbContext, cancellationToken),
            await FFmpegProfileMustExist(dbContext, request, cancellationToken))
        .Apply((mediaItem, ffmpegPath, ffprobePath, ffmpegProfile) =>
            Tuple(mediaItem, ffmpegPath, ffprobePath, ffmpegProfile));

    private static Task<Validation<BaseError, string>> FFprobePathMustExist(
        TvContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.ConfigElements.GetValue<string>(ConfigElementKey.FFprobePath, cancellationToken)
            .FilterT(File.Exists)
            .Map(maybePath => maybePath.ToValidation<BaseError>("FFprobe path does not exist on filesystem"));

    private static Task<Validation<BaseError, FFmpegProfile>> FFmpegProfileMustExist(
        TvContext dbContext,
        PrepareTroubleshootingPlayback request,
        CancellationToken cancellationToken) =>
        dbContext.FFmpegProfiles
            .Include(p => p.Resolution)
            .SelectOneAsync(p => p.Id, p => p.Id == request.FFmpegProfileId, cancellationToken)
            .Map(o => o.ToValidation<BaseError>($"FFmpegProfile {request.FFmpegProfileId} does not exist"));
}

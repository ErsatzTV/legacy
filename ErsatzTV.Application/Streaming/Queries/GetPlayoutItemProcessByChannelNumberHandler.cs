using System.IO.Abstractions;
using CliWrap;
using Dapper;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.State;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetPlayoutItemProcessByChannelNumberHandler : FFmpegProcessHandler<GetPlayoutItemProcessByChannelNumber>
{
    private static readonly Random FallbackRandom = new();

    private readonly IArtistRepository _artistRepository;
    private readonly IEmbyPathReplacementService _embyPathReplacementService;
    private readonly IExternalJsonPlayoutItemProvider _externalJsonPlayoutItemProvider;
    private readonly IFFmpegProcessService _ffmpegProcessService;
    private readonly IFileSystem _fileSystem;
    private readonly IJellyfinPathReplacementService _jellyfinPathReplacementService;
    private readonly ILogger<GetPlayoutItemProcessByChannelNumberHandler> _logger;
    private readonly IMediaCollectionRepository _mediaCollectionRepository;
    private readonly IMusicVideoCreditsGenerator _musicVideoCreditsGenerator;
    private readonly IWatermarkSelector _watermarkSelector;
    private readonly IGraphicsElementSelector _graphicsElementSelector;
    private readonly IDecoSelector _decoSelector;
    private readonly IPlexPathReplacementService _plexPathReplacementService;
    private readonly ISongVideoGenerator _songVideoGenerator;
    private readonly ITelevisionRepository _televisionRepository;
    private readonly bool _isDebugNoSync;

    public GetPlayoutItemProcessByChannelNumberHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IFFmpegProcessService ffmpegProcessService,
        IFileSystem fileSystem,
        IExternalJsonPlayoutItemProvider externalJsonPlayoutItemProvider,
        IPlexPathReplacementService plexPathReplacementService,
        IJellyfinPathReplacementService jellyfinPathReplacementService,
        IEmbyPathReplacementService embyPathReplacementService,
        IMediaCollectionRepository mediaCollectionRepository,
        ITelevisionRepository televisionRepository,
        IArtistRepository artistRepository,
        ISongVideoGenerator songVideoGenerator,
        IMusicVideoCreditsGenerator musicVideoCreditsGenerator,
        IWatermarkSelector watermarkSelector,
        IGraphicsElementSelector graphicsElementSelector,
        IDecoSelector decoSelector,
        ILogger<GetPlayoutItemProcessByChannelNumberHandler> logger)
        : base(dbContextFactory)
    {
        _ffmpegProcessService = ffmpegProcessService;
        _fileSystem = fileSystem;
        _externalJsonPlayoutItemProvider = externalJsonPlayoutItemProvider;
        _plexPathReplacementService = plexPathReplacementService;
        _jellyfinPathReplacementService = jellyfinPathReplacementService;
        _embyPathReplacementService = embyPathReplacementService;
        _mediaCollectionRepository = mediaCollectionRepository;
        _televisionRepository = televisionRepository;
        _artistRepository = artistRepository;
        _songVideoGenerator = songVideoGenerator;
        _musicVideoCreditsGenerator = musicVideoCreditsGenerator;
        _watermarkSelector = watermarkSelector;
        _graphicsElementSelector = graphicsElementSelector;
        _decoSelector = decoSelector;
        _logger = logger;

#if DEBUG_NO_SYNC
        _isDebugNoSync = true;
#else
        _isDebugNoSync = false;
#endif
    }

    protected override async Task<Either<BaseError, PlayoutItemProcessModel>> GetProcess(
        TvContext dbContext,
        GetPlayoutItemProcessByChannelNumber request,
        Channel channel,
        string ffmpegPath,
        string ffprobePath,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = request.Now;

        Either<BaseError, PlayoutItemWithPath> maybePlayoutItem = await dbContext.PlayoutItems
            .AsNoTracking()

            // get playout deco
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Deco)
            .ThenInclude(d => d.DecoGraphicsElements)
            .ThenInclude(d => d.GraphicsElement)

            // get graphics elements
            .Include(i => i.PlayoutItemGraphicsElements)
            .ThenInclude(pige => pige.GraphicsElement)

            // get playout templates (and deco templates/decos)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Templates)
            .ThenInclude(t => t.DecoTemplate)
            .ThenInclude(t => t.Items)
            .ThenInclude(i => i.Deco)
            .ThenInclude(d => d.DecoWatermarks)
            .ThenInclude(d => d.Watermark)
            .Include(i => i.Playout)
            .ThenInclude(p => p.Templates)
            .ThenInclude(t => t.DecoTemplate)
            .ThenInclude(t => t.Items)
            .ThenInclude(i => i.Deco)
            .ThenInclude(d => d.DecoGraphicsElements)
            .ThenInclude(d => d.GraphicsElement)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Episode).EpisodeMetadata)
            .ThenInclude(em => em.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Episode).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Episode).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Episode).Season)
            .ThenInclude(s => s.Show)
            .ThenInclude(s => s.ShowMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Movie).MovieMetadata)
            .ThenInclude(mm => mm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Movie).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Movie).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Artists)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Studios)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Directors)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as MusicVideo).Artist)
            .ThenInclude(mv => mv.ArtistMetadata)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as OtherVideo).OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Subtitles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as OtherVideo).MediaVersions)
            .ThenInclude(ov => ov.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as OtherVideo).MediaVersions)
            .ThenInclude(ov => ov.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Song).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Song).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Song).SongMetadata)
            .ThenInclude(sm => sm.Artwork)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Image).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Image).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as Image).ImageMetadata)
            .Include(i => i.Watermarks)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as RemoteStream).MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(i => i.MediaItem)
            .ThenInclude(mi => (mi as RemoteStream).RemoteStreamMetadata)
            .Include(i => i.Watermarks)
            .ForChannelAndTime(channel.MirrorSourceChannelId ?? channel.Id, now)
            .Map(o => o.ToEither<BaseError>(new UnableToLocatePlayoutItem()))
            .BindT(item => ValidatePlayoutItemPath(dbContext, item, cancellationToken));

        if (maybePlayoutItem.LeftAsEnumerable().Any(e => e is UnableToLocatePlayoutItem))
        {
            maybePlayoutItem = await _externalJsonPlayoutItemProvider.CheckForExternalJson(
                channel,
                now,
                ffprobePath,
                cancellationToken);
        }

        if (maybePlayoutItem.LeftAsEnumerable().Any(e => e is UnableToLocatePlayoutItem))
        {
            Option<Playout> maybePlayout = await dbContext.Playouts
                .AsNoTracking()

                // get playout deco
                .Include(p => p.Deco)
                .ThenInclude(d => d.DecoWatermarks)
                .ThenInclude(d => d.Watermark)
                .Include(p => p.Deco)
                .ThenInclude(d => d.DecoGraphicsElements)
                .ThenInclude(d => d.GraphicsElement)

                // get playout templates (and deco templates/decos)
                .Include(p => p.Templates)
                .ThenInclude(t => t.DecoTemplate)
                .ThenInclude(t => t.Items)
                .ThenInclude(i => i.Deco)
                .ThenInclude(d => d.DecoWatermarks)
                .ThenInclude(d => d.Watermark)
                .SelectOneAsync(
                    p => p.ChannelId,
                    p => p.ChannelId == (channel.MirrorSourceChannelId ?? channel.Id),
                    cancellationToken);

            foreach (Playout playout in maybePlayout)
            {
                maybePlayoutItem = await CheckForFallbackFiller(dbContext, channel, playout, now, cancellationToken);
            }

            if (maybePlayout.IsNone)
            {
                maybePlayoutItem = await CheckForFallbackFiller(dbContext, channel, null, now, cancellationToken);
            }
        }

        foreach (PlayoutItemWithPath playoutItemWithPath in maybePlayoutItem.RightToSeq())
        {
            try
            {
                PlayoutItemViewModel viewModel = Mapper.ProjectToViewModel(playoutItemWithPath.PlayoutItem);
                if (!string.IsNullOrWhiteSpace(viewModel.Title))
                {
                    _logger.LogDebug(
                        "Found playout item {Title} with path {Path}",
                        viewModel.Title,
                        playoutItemWithPath.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get playout item title");
            }

            DateTimeOffset start = playoutItemWithPath.PlayoutItem.StartOffset;
            DateTimeOffset finish = playoutItemWithPath.PlayoutItem.FinishOffset;
            TimeSpan inPoint = playoutItemWithPath.PlayoutItem.InPoint;
            TimeSpan outPoint = playoutItemWithPath.PlayoutItem.OutPoint;
            DateTimeOffset effectiveNow = request.StartAtZero ? start : now;
            TimeSpan duration = finish - effectiveNow;
            TimeSpan originalDuration = duration;

            bool isComplete = true;

            bool effectiveRealtime = request.HlsRealtime;

            // only work ahead on fallback filler up to 3 minutes in duration
            // since we always transcode a full fallback filler item
            if (!effectiveRealtime &&
                playoutItemWithPath.PlayoutItem.FillerKind is FillerKind.Fallback &&
                duration > TimeSpan.FromMinutes(3))
            {
                effectiveRealtime = true;
            }

            TimeSpan limit = TimeSpan.Zero;

            if (!effectiveRealtime)
            {
                // if we are working ahead, limit to 44s (multiple of segment size)
                limit = TimeSpan.FromSeconds(44);
            }

            if (request.IsTroubleshooting)
            {
                // if we are troubleshooting, limit to 30s
                limit = TimeSpan.FromSeconds(30);
            }

            if (limit > TimeSpan.Zero && duration > limit)
            {
                finish = effectiveNow + limit;
                outPoint = inPoint + limit;
                duration = limit;
                isComplete = false;
            }

            if (request.IsTroubleshooting)
            {
                channel.Number = FileSystemLayout.TranscodeTroubleshootingChannel;
            }

            if (_isDebugNoSync)
            {
                Command doesNotExistProcess = await _ffmpegProcessService.ForError(
                    ffmpegPath,
                    channel,
                    now,
                    duration,
                    $"DEBUG_NO_SYNC:\n{Mapper.GetDisplayTitle(playoutItemWithPath.PlayoutItem.MediaItem, Option<string>.None)}\nFrom: {start} To: {finish}",
                    effectiveRealtime,
                    request.PtsOffset,
                    channel.FFmpegProfile.VaapiDisplay,
                    channel.FFmpegProfile.VaapiDriver,
                    channel.FFmpegProfile.VaapiDevice,
                    Optional(channel.FFmpegProfile.QsvExtraHardwareFrames),
                    cancellationToken);

                return new PlayoutItemProcessModel(
                    doesNotExistProcess,
                    Option<GraphicsEngineContext>.None,
                    duration,
                    finish,
                    true,
                    effectiveNow.ToUnixTimeSeconds(),
                    Option<int>.None,
                    Optional(channel.PlayoutOffset),
                    !effectiveRealtime);
            }

            MediaVersion version = playoutItemWithPath.PlayoutItem.MediaItem.GetHeadVersion();

            string videoPath = playoutItemWithPath.Path;
            MediaVersion videoVersion = version;

            string audioPath = playoutItemWithPath.Path;
            MediaVersion audioVersion = version;

            Option<ChannelWatermark> maybeGlobalWatermark = await dbContext.ConfigElements
                .GetValue<int>(ConfigElementKey.FFmpegGlobalWatermarkId, cancellationToken)
                .BindT(watermarkId => dbContext.ChannelWatermarks
                    .SelectOneAsync(w => w.Id, w => w.Id == watermarkId, cancellationToken));

            List<WatermarkOptions> watermarks = _watermarkSelector.SelectWatermarks(
                maybeGlobalWatermark,
                channel,
                playoutItemWithPath.PlayoutItem,
                now,
                shouldLogMessages: true);

            if (playoutItemWithPath.PlayoutItem.MediaItem is Song song)
            {
                (videoPath, videoVersion) = await _songVideoGenerator.GenerateSongVideo(
                    song,
                    channel,
                    ffmpegPath,
                    ffprobePath,
                    cancellationToken);

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

            List<PlayoutItemGraphicsElement> graphicsElements = _graphicsElementSelector.SelectGraphicsElements(
                channel,
                playoutItemWithPath.PlayoutItem,
                now);

            if (playoutItemWithPath.PlayoutItem.MediaItem is Image)
            {
                audioPath = string.Empty;
            }

            bool saveReports = await dbContext.ConfigElements
                .GetValue<bool>(ConfigElementKey.FFmpegSaveReports, cancellationToken)
                .Map(result => result.IfNone(false)) || request.IsTroubleshooting;

            _logger.LogDebug(
                "S: {Start}, F: {Finish}, In: {InPoint}, Out: {OutPoint}, EffNow: {EffectiveNow}, Dur: {Duration}",
                start,
                finish,
                inPoint,
                outPoint,
                effectiveNow,
                duration);

            PlayoutItemResult playoutItemResult = await _ffmpegProcessService.ForPlayoutItem(
                ffmpegPath,
                ffprobePath,
                saveReports,
                channel,
                new MediaItemVideoVersion(playoutItemWithPath.PlayoutItem.MediaItem, videoVersion),
                new MediaItemAudioVersion(playoutItemWithPath.PlayoutItem.MediaItem, audioVersion),
                videoPath,
                audioPath,
                settings => GetSubtitles(playoutItemWithPath, channel, settings),
                playoutItemWithPath.PlayoutItem.PreferredAudioLanguageCode ?? channel.PreferredAudioLanguageCode,
                playoutItemWithPath.PlayoutItem.PreferredAudioTitle ?? channel.PreferredAudioTitle,
                playoutItemWithPath.PlayoutItem.PreferredSubtitleLanguageCode ?? channel.PreferredSubtitleLanguageCode,
                playoutItemWithPath.PlayoutItem.SubtitleMode ?? channel.SubtitleMode,
                start,
                finish,
                effectiveNow,
                originalDuration,
                watermarks,
                graphicsElements,
                channel.FFmpegProfile.VaapiDisplay,
                channel.FFmpegProfile.VaapiDriver,
                channel.FFmpegProfile.VaapiDevice,
                Optional(channel.FFmpegProfile.QsvExtraHardwareFrames),
                effectiveRealtime,
                playoutItemWithPath.PlayoutItem.MediaItem is RemoteStream { IsLive: true }
                    ? StreamInputKind.Live
                    : StreamInputKind.Vod,
                playoutItemWithPath.PlayoutItem.FillerKind,
                inPoint,
                request.ChannelStartTime,
                request.PtsOffset,
                request.TargetFramerate,
                request.IsTroubleshooting ? FileSystemLayout.TranscodeTroubleshootingFolder : Option<string>.None,
                _ => { },
                canProxy: true,
                cancellationToken);

            var result = new PlayoutItemProcessModel(
                playoutItemResult.Process,
                playoutItemResult.GraphicsEngineContext,
                duration,
                finish,
                isComplete,
                effectiveNow.ToUnixTimeSeconds(),
                playoutItemResult.MediaItemId,
                Optional(channel.PlayoutOffset),
                !effectiveRealtime);

            return Right<BaseError, PlayoutItemProcessModel>(result);
        }

        foreach (BaseError error in maybePlayoutItem.LeftToSeq())
        {
            Option<DateTimeOffset> maybeNextStart = await dbContext.PlayoutItems
                .Filter(pi => pi.Playout.ChannelId == (channel.MirrorSourceChannelId ?? channel.Id))
                .Filter(pi => pi.Start > now.UtcDateTime)
                .OrderBy(pi => pi.Start)
                .FirstOrDefaultAsync(cancellationToken)
                .Map(Optional)
                .MapT(pi => pi.StartOffset);

            Option<TimeSpan> maybeDuration = maybeNextStart.Map(s => s - now);

            // limit working ahead on errors to 1 minute
            if (!request.HlsRealtime && await maybeDuration.IfNoneAsync(TimeSpan.FromMinutes(2)) > TimeSpan.FromMinutes(1))
            {
                maybeNextStart = now.AddMinutes(1);
                maybeDuration = TimeSpan.FromMinutes(1);
            }

            DateTimeOffset finish = maybeNextStart.Match(s => s, () => now);

            if (request.IsTroubleshooting)
            {
                channel.Number = FileSystemLayout.TranscodeTroubleshootingChannel;

                maybeDuration = TimeSpan.FromSeconds(30);
                finish = now + TimeSpan.FromSeconds(30);
            }

            _logger.LogWarning(
                "Error locating playout item {@Error}. Will display error from {Start} to {Finish}",
                error,
                now,
                finish);

            switch (error)
            {
                case UnableToLocatePlayoutItem:
                    Command offlineProcess = await _ffmpegProcessService.ForError(
                        ffmpegPath,
                        channel,
                        now,
                        maybeDuration,
                        "Channel is Offline",
                        request.HlsRealtime,
                        request.PtsOffset,
                        channel.FFmpegProfile.VaapiDisplay,
                        channel.FFmpegProfile.VaapiDriver,
                        channel.FFmpegProfile.VaapiDevice,
                        Optional(channel.FFmpegProfile.QsvExtraHardwareFrames),
                        cancellationToken);

                    return new PlayoutItemProcessModel(
                        offlineProcess,
                        Option<GraphicsEngineContext>.None,
                        maybeDuration,
                        finish,
                        true,
                        now.ToUnixTimeSeconds(),
                        Option<int>.None,
                        Optional(channel.PlayoutOffset),
                        !request.HlsRealtime);
                case PlayoutItemDoesNotExistOnDisk:
                    Command doesNotExistProcess = await _ffmpegProcessService.ForError(
                        ffmpegPath,
                        channel,
                        now,
                        maybeDuration,
                        error.Value,
                        request.HlsRealtime,
                        request.PtsOffset,
                        channel.FFmpegProfile.VaapiDisplay,
                        channel.FFmpegProfile.VaapiDriver,
                        channel.FFmpegProfile.VaapiDevice,
                        Optional(channel.FFmpegProfile.QsvExtraHardwareFrames),
                        cancellationToken);

                    return new PlayoutItemProcessModel(
                        doesNotExistProcess,
                        Option<GraphicsEngineContext>.None,
                        maybeDuration,
                        finish,
                        true,
                        now.ToUnixTimeSeconds(),
                        Option<int>.None,
                        Optional(channel.PlayoutOffset),
                        !request.HlsRealtime);
                default:
                    Command errorProcess = await _ffmpegProcessService.ForError(
                        ffmpegPath,
                        channel,
                        now,
                        maybeDuration,
                        "Channel is Offline",
                        request.HlsRealtime,
                        request.PtsOffset,
                        channel.FFmpegProfile.VaapiDisplay,
                        channel.FFmpegProfile.VaapiDriver,
                        channel.FFmpegProfile.VaapiDevice,
                        Optional(channel.FFmpegProfile.QsvExtraHardwareFrames),
                        cancellationToken);

                    return new PlayoutItemProcessModel(
                        errorProcess,
                        Option<GraphicsEngineContext>.None,
                        maybeDuration,
                        finish,
                        true,
                        now.ToUnixTimeSeconds(),
                        Option<int>.None,
                        Optional(channel.PlayoutOffset),
                        !request.HlsRealtime);
            }
        }

        return BaseError.New($"Unexpected error locating playout item for channel {channel.Number}");
    }

    private async Task<List<Subtitle>> GetSubtitles(
        PlayoutItemWithPath playoutItemWithPath,
        Channel channel,
        FFmpegPlaybackSettings settings)
    {
        List<Subtitle> allSubtitles = playoutItemWithPath.PlayoutItem.MediaItem switch
        {
            Episode episode => await Optional(episode.EpisodeMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            Movie movie => await Optional(movie.MovieMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            MusicVideo musicVideo => await GetMusicVideoSubtitles(musicVideo, channel, settings),
            OtherVideo otherVideo => await Optional(otherVideo.OtherVideoMetadata).Flatten().HeadOrNone()
                .Map(mm => mm.Subtitles ?? [])
                .IfNoneAsync([]),
            _ => []
        };

        bool isMediaServer = playoutItemWithPath.PlayoutItem.MediaItem is PlexMovie or PlexEpisode or
            JellyfinMovie or JellyfinEpisode or EmbyMovie or EmbyEpisode;

        if (isMediaServer)
        {
            // closed captions are currently unsupported
            allSubtitles.RemoveAll(s => s.Codec == "eia_608");
        }

        return allSubtitles;
    }

    private async Task<List<Subtitle>> GetMusicVideoSubtitles(
        MusicVideo musicVideo,
        Channel channel,
        FFmpegPlaybackSettings settings)
    {
        var subtitles = new List<Subtitle>();

        switch (channel.MusicVideoCreditsMode)
        {
            case ChannelMusicVideoCreditsMode.GenerateSubtitles:
                var fileWithExtension = $"{channel.MusicVideoCreditsTemplate}.sbntxt";
                if (!string.IsNullOrWhiteSpace(fileWithExtension))
                {
                    subtitles.AddRange(
                        await _musicVideoCreditsGenerator.GenerateCreditsSubtitleFromTemplate(
                            musicVideo,
                            channel.FFmpegProfile,
                            settings.StreamSeek,
                            Path.Combine(FileSystemLayout.MusicVideoCreditsTemplatesFolder, fileWithExtension)));
                }
                else
                {
                    _logger.LogWarning(
                        "Music video credits template {Template} does not exist; falling back to built-in template",
                        fileWithExtension);

                    subtitles.AddRange(
                        await _musicVideoCreditsGenerator.GenerateCreditsSubtitle(musicVideo, channel.FFmpegProfile));
                }

                break;
            case ChannelMusicVideoCreditsMode.None:
            default:
                subtitles.AddRange(
                    await Optional(musicVideo.MusicVideoMetadata).Flatten().HeadOrNone()
                        .Map(mm => mm.Subtitles)
                        .IfNoneAsync([]));
                break;
        }

        return subtitles;
    }

    private async Task<Either<BaseError, PlayoutItemWithPath>> CheckForFallbackFiller(
        TvContext dbContext,
        Channel channel,
        Playout playout,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Option<FillerPreset> maybeFallback = Option<FillerPreset>.None;

        DeadAirFallbackResult decoDeadAirFallback = GetDecoDeadAirFallback(playout, now);
        switch (decoDeadAirFallback)
        {
            case CustomDeadAirFallback custom:
                maybeFallback = new FillerPreset
                {
                    // always allow watermarks here
                    // deco settings will disable watermarks if appropriate
                    AllowWatermarks = true,

                    CollectionType = custom.CollectionType,
                    CollectionId = custom.CollectionId,
                    MediaItemId = custom.MediaItemId,
                    MultiCollectionId = custom.MultiCollectionId,
                    SmartCollectionId = custom.SmartCollectionId
                };
                break;
            case DisableDeadAirFallback:
                // do nothing
                break;
            case InheritDeadAirFallback:
                // check for channel fallback
                maybeFallback = await dbContext.FillerPresets
                    .SelectOneAsync(w => w.Id, w => w.Id == channel.FallbackFillerId, cancellationToken);

                // then check for global fallback
                if (maybeFallback.IsNone)
                {
                    maybeFallback = await dbContext.ConfigElements
                        .GetValue<int>(ConfigElementKey.FFmpegGlobalFallbackFillerId, cancellationToken)
                        .BindT(fillerId => dbContext.FillerPresets.SelectOneAsync(
                            w => w.Id,
                            w => w.Id == fillerId,
                            cancellationToken));
                }

                break;
        }


        foreach (FillerPreset fallbackPreset in maybeFallback)
        {
            // turn this into a playout item

            var collectionKey = CollectionKey.ForFillerPreset(fallbackPreset);
            List<MediaItem> items = await MediaItemsForCollection.Collect(
                _mediaCollectionRepository,
                _televisionRepository,
                _artistRepository,
                collectionKey,
                cancellationToken);

            // ignore the fallback filler preset if it has no items
            if (items.Count == 0)
            {
                break;
            }

            // get a random item
            MediaItem item = items[FallbackRandom.Next(items.Count)];

            Option<TimeSpan> maybeDuration = await dbContext.PlayoutItems
                .Filter(pi => pi.Playout.ChannelId == (channel.MirrorSourceChannelId ?? channel.Id))
                .Filter(pi => pi.Start > now.UtcDateTime)
                .OrderBy(pi => pi.Start)
                .FirstOrDefaultAsync(cancellationToken)
                .Map(Optional)
                .MapT(pi => pi.StartOffset - now);

            MediaVersion version = item.GetHeadVersion();

            version.MediaFiles = await dbContext.MediaFiles
                .AsNoTracking()
                .Filter(mf => mf.MediaVersionId == version.Id)
                .ToListAsync(cancellationToken);

            version.Streams = await dbContext.MediaStreams
                .AsNoTracking()
                .Filter(ms => ms.MediaVersionId == version.Id)
                .ToListAsync(cancellationToken);

            // always play min(duration to next item, version.Duration)
            TimeSpan duration = await maybeDuration.IfNoneAsync(version.Duration);
            if (version.Duration < duration)
            {
                duration = version.Duration;
            }

            DateTimeOffset finish = now.Add(duration);

            var playoutItem = new PlayoutItem
            {
                MediaItem = item,
                MediaItemId = item.Id,
                Start = now.UtcDateTime,
                Finish = finish.UtcDateTime,
                FillerKind = FillerKind.Fallback,
                InPoint = TimeSpan.Zero,
                OutPoint = duration,
                DisableWatermarks = !fallbackPreset.AllowWatermarks,
                Watermarks = [],
                PlayoutItemWatermarks = [],
                GraphicsElements = [],
                PlayoutItemGraphicsElements = []
            };

            return await ValidatePlayoutItemPath(dbContext, playoutItem, cancellationToken);
        }

        return new UnableToLocatePlayoutItem();
    }

    private async Task<Either<BaseError, PlayoutItemWithPath>> ValidatePlayoutItemPath(
        TvContext dbContext,
        PlayoutItem playoutItem,
        CancellationToken cancellationToken)
    {
        string path = await playoutItem.MediaItem.GetLocalPath(
            _plexPathReplacementService,
            _jellyfinPathReplacementService,
            _embyPathReplacementService,
            cancellationToken);

        if (_isDebugNoSync)
        {
            // pretend it exists so we get a nice error message
            return new PlayoutItemWithPath(playoutItem, path);
        }

        // check filesystem first
        if (_fileSystem.File.Exists(path))
        {
            if (playoutItem.MediaItem is RemoteStream remoteStream)
            {
                path = !string.IsNullOrWhiteSpace(remoteStream.Url)
                    ? remoteStream.Url
                    : $"http://localhost:{Settings.StreamingPort}/ffmpeg/remote-stream/{remoteStream.Id}";
            }

            return new PlayoutItemWithPath(playoutItem, path);
        }

        // attempt to remotely stream plex
        MediaFile file = playoutItem.MediaItem.GetHeadVersion().MediaFiles.Head();
        switch (file)
        {
            case PlexMediaFile pmf:
                Option<int> maybeId = await dbContext.Connection.QuerySingleOrDefaultAsync<int>(
                        @"SELECT PMS.Id FROM PlexMediaSource PMS
                  INNER JOIN Library L on PMS.Id = L.MediaSourceId
                  INNER JOIN LibraryPath LP on L.Id = LP.LibraryId
                  WHERE LP.Id = @LibraryPathId",
                        new { playoutItem.MediaItem.LibraryPathId })
                    .Map(Optional);

                foreach (int plexMediaSourceId in maybeId)
                {
                    _logger.LogDebug(
                        "Attempting to stream Plex file {PlexFileName} using key {PlexKey}",
                        pmf.Path,
                        pmf.Key);

                    return new PlayoutItemWithPath(
                        playoutItem,
                        $"http://localhost:{Settings.StreamingPort}/media/plex/{plexMediaSourceId}/{pmf.Key}");
                }

                break;
        }

        // attempt to remotely stream jellyfin
        Option<string> jellyfinItemId = playoutItem.MediaItem switch
        {
            JellyfinEpisode e => e.ItemId,
            JellyfinMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in jellyfinItemId)
        {
            return new PlayoutItemWithPath(
                playoutItem,
                $"http://localhost:{Settings.StreamingPort}/media/jellyfin/{itemId}");
        }

        // attempt to remotely stream emby
        Option<string> embyItemId = playoutItem.MediaItem switch
        {
            EmbyEpisode e => e.ItemId,
            EmbyMovie m => m.ItemId,
            _ => None
        };

        foreach (string itemId in embyItemId)
        {
            return new PlayoutItemWithPath(
                playoutItem,
                $"http://localhost:{Settings.StreamingPort}/media/emby/{itemId}");
        }

        return new PlayoutItemDoesNotExistOnDisk(path);
    }

    private DeadAirFallbackResult GetDecoDeadAirFallback(Playout playout, DateTimeOffset now)
    {
        DecoEntries decoEntries = _decoSelector.GetDecoEntries(playout, now);

        // first, check deco template / active deco
        foreach (Deco templateDeco in decoEntries.TemplateDeco)
        {
            switch (templateDeco.DeadAirFallbackMode)
            {
                case DecoMode.Override:
                    _logger.LogDebug("Dead air fallback will come from template deco (override)");
                    return new CustomDeadAirFallback(
                        templateDeco.DeadAirFallbackCollectionType,
                        templateDeco.DeadAirFallbackCollectionId,
                        templateDeco.DeadAirFallbackMediaItemId,
                        templateDeco.DeadAirFallbackMultiCollectionId,
                        templateDeco.DeadAirFallbackSmartCollectionId);
                case DecoMode.Disable:
                    _logger.LogDebug("Dead air fallback is disabled by template deco");
                    return new DisableDeadAirFallback();
                case DecoMode.Inherit:
                    _logger.LogDebug("Dead air fallback will inherit from playout deco");
                    break;
            }
        }

        // second, check playout deco
        foreach (Deco playoutDeco in decoEntries.PlayoutDeco)
        {
            switch (playoutDeco.DeadAirFallbackMode)
            {
                case DecoMode.Override:
                    _logger.LogDebug("Dead air fallback will come from playout deco (override)");
                    return new CustomDeadAirFallback(
                        playoutDeco.DeadAirFallbackCollectionType,
                        playoutDeco.DeadAirFallbackCollectionId,
                        playoutDeco.DeadAirFallbackMediaItemId,
                        playoutDeco.DeadAirFallbackMultiCollectionId,
                        playoutDeco.DeadAirFallbackSmartCollectionId);
                case DecoMode.Disable:
                    _logger.LogDebug("Dead air fallback is disabled by playout deco");
                    return new DisableDeadAirFallback();
                case DecoMode.Inherit:
                    _logger.LogDebug("Dead air fallback will inherit from channel and/or global setting");
                    break;
            }
        }

        return new InheritDeadAirFallback();
    }

    private abstract record DeadAirFallbackResult;

    private sealed record InheritDeadAirFallback : DeadAirFallbackResult;

    private sealed record DisableDeadAirFallback : DeadAirFallbackResult;

    private sealed record CustomDeadAirFallback(
        CollectionType CollectionType,
        int? CollectionId,
        int? MediaItemId,
        int? MultiCollectionId,
        int? SmartCollectionId) : DeadAirFallbackResult;
}

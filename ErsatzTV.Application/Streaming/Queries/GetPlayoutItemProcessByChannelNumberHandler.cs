using CliWrap;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg;
using ErsatzTV.FFmpeg.State;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using ErsatzTV.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class GetPlayoutItemProcessByChannelNumberHandler : FFmpegProcessHandler<GetPlayoutItemProcessByChannelNumber>
{
    private readonly IExternalJsonPlayoutItemProvider _externalJsonPlayoutItemProvider;
    private readonly IFFmpegProcessService _ffmpegProcessService;
    private readonly ILogger<GetPlayoutItemProcessByChannelNumberHandler> _logger;
    private readonly IMusicVideoCreditsGenerator _musicVideoCreditsGenerator;
    private readonly IWatermarkSelector _watermarkSelector;
    private readonly IGraphicsElementSelector _graphicsElementSelector;
    private readonly IDynamicPlayoutItemService _dynamicPlayoutItemService;
    private readonly ISongVideoGenerator _songVideoGenerator;
    private readonly bool _isDebugNoSync;

    public GetPlayoutItemProcessByChannelNumberHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IFFmpegProcessService ffmpegProcessService,
        IExternalJsonPlayoutItemProvider externalJsonPlayoutItemProvider,
        ISongVideoGenerator songVideoGenerator,
        IMusicVideoCreditsGenerator musicVideoCreditsGenerator,
        IWatermarkSelector watermarkSelector,
        IGraphicsElementSelector graphicsElementSelector,
        IDynamicPlayoutItemService dynamicPlayoutItemService,
        ILogger<GetPlayoutItemProcessByChannelNumberHandler> logger)
        : base(dbContextFactory)
    {
        _ffmpegProcessService = ffmpegProcessService;
        _externalJsonPlayoutItemProvider = externalJsonPlayoutItemProvider;
        _songVideoGenerator = songVideoGenerator;
        _musicVideoCreditsGenerator = musicVideoCreditsGenerator;
        _watermarkSelector = watermarkSelector;
        _graphicsElementSelector = graphicsElementSelector;
        _dynamicPlayoutItemService = dynamicPlayoutItemService;
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
            .BindT(item => _dynamicPlayoutItemService.ValidatePlayoutItemPath(dbContext, item, cancellationToken));

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
            maybePlayoutItem = await _dynamicPlayoutItemService.CheckForFallbackFiller(
                dbContext,
                channel,
                now,
                cancellationToken);
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
}

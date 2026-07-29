using System.Globalization;
using System.IO.Abstractions;
using System.Xml;
using ErsatzTV.Application.Configuration;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Emby;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Iptv;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IO;
using Newtonsoft.Json;
using Scriban;
using Scriban.Runtime;
using WebMarkupMin.Core;

namespace ErsatzTV.Application.Channels;

public class RefreshChannelDataHandler : IRequestHandler<RefreshChannelData>
{
    private readonly IConfigElementRepository _configElementRepository;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILogger<RefreshChannelDataHandler> _logger;
    private readonly RecyclableMemoryStreamManager _recyclableMemoryStreamManager;

    public RefreshChannelDataHandler(
        RecyclableMemoryStreamManager recyclableMemoryStreamManager,
        IDbContextFactory<TvContext> dbContextFactory,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        IConfigElementRepository configElementRepository,
        ILogger<RefreshChannelDataHandler> logger)
    {
        _recyclableMemoryStreamManager = recyclableMemoryStreamManager;
        _dbContextFactory = dbContextFactory;
        _fileSystem = fileSystem;
        _localFileSystem = localFileSystem;
        _configElementRepository = configElementRepository;
        _logger = logger;
    }

    public async Task Handle(RefreshChannelData request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Refreshing channel data (XMLTV) for channel {Channel}", request.ChannelNumber);

            _localFileSystem.EnsureFolderExists(FileSystemLayout.ChannelGuideCacheFolder);

            string targetFile = Path.Combine(FileSystemLayout.ChannelGuideCacheFolder, $"{request.ChannelNumber}.xml");
            await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            int hiddenCount = await dbContext.Channels
                .Where(c => c.Number == request.ChannelNumber && c.ShowInEpg == false)
                .CountAsync(cancellationToken);
            if (hiddenCount > 0)
            {
                File.Delete(targetFile);
                return;
            }

            string movieTemplateFileName = GetMovieTemplateFileName();
            string episodeTemplateFileName = GetEpisodeTemplateFileName();
            string musicVideoTemplateFileName = GetMusicVideoTemplateFileName();
            string songTemplateFileName = GetSongTemplateFileName();
            string otherVideoTemplateFileName = GetOtherVideoTemplateFileName();
            string remoteStreamTemplateFileName = GetRemoteStreamTemplateFileName();
            if (movieTemplateFileName is null || episodeTemplateFileName is null ||
                musicVideoTemplateFileName is null ||
                songTemplateFileName is null || otherVideoTemplateFileName is null ||
                remoteStreamTemplateFileName is null)
            {
                return;
            }

            var minifier = new XmlMinifier(
                new XmlMinificationSettings
                {
                    MinifyWhitespace = true,
                    RemoveXmlComments = true,
                    CollapseTagsWithoutContent = true
                });

            var templateContext = new XmlTemplateContext();

            string movieText = await File.ReadAllTextAsync(movieTemplateFileName, cancellationToken);
            var movieTemplate = Template.Parse(movieText, movieTemplateFileName);

            string episodeText = await File.ReadAllTextAsync(episodeTemplateFileName, cancellationToken);
            var episodeTemplate = Template.Parse(episodeText, episodeTemplateFileName);

            string musicVideoText = await File.ReadAllTextAsync(musicVideoTemplateFileName, cancellationToken);
            var musicVideoTemplate = Template.Parse(musicVideoText, musicVideoTemplateFileName);

            string songText = await File.ReadAllTextAsync(songTemplateFileName, cancellationToken);
            var songTemplate = Template.Parse(songText, songTemplateFileName);

            string otherVideoText = await File.ReadAllTextAsync(otherVideoTemplateFileName, cancellationToken);
            var otherVideoTemplate = Template.Parse(otherVideoText, otherVideoTemplateFileName);

            string remoteStreamText = await File.ReadAllTextAsync(remoteStreamTemplateFileName, cancellationToken);
            var remoteStreamTemplate = Template.Parse(remoteStreamText, remoteStreamTemplateFileName);

            TimeSpan playoutOffset = TimeSpan.Zero;
            string mirrorChannelNumber = null;
            Option<Channel> maybeChannel = await dbContext.Channels
                .AsNoTracking()
                .Include(c => c.MirrorSourceChannel)
                .Filter(c => c.PlayoutSource == ChannelPlayoutSource.Mirror && c.MirrorSourceChannelId != null)
                .SelectOneAsync(
                    c => c.Number == request.ChannelNumber,
                    c => c.Number == request.ChannelNumber,
                    cancellationToken);
            foreach (Channel channel in maybeChannel)
            {
                mirrorChannelNumber = channel.MirrorSourceChannel.Number;
                playoutOffset = channel.PlayoutOffset ?? TimeSpan.Zero;
            }

            List<Playout> playouts = await dbContext.Playouts
                .AsNoTracking()
                .Filter(pi => pi.Channel.Number == (mirrorChannelNumber ?? request.ChannelNumber))
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Episode).EpisodeMetadata)
                .ThenInclude(em => em.Guids)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Episode).EpisodeMetadata)
                .ThenInclude(em => em.Artwork)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Episode).Season)
                .ThenInclude(s => s.Show)
                .ThenInclude(s => s.ShowMetadata)
                .ThenInclude(sm => sm.Artwork)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Episode).Season)
                .ThenInclude(s => s.Show)
                .ThenInclude(s => s.ShowMetadata)
                .ThenInclude(sm => sm.Genres)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Episode).Season)
                .ThenInclude(s => s.Show)
                .ThenInclude(s => s.ShowMetadata)
                .ThenInclude(sm => sm.Guids)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Movie).MovieMetadata)
                .ThenInclude(mm => mm.Artwork)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Movie).MovieMetadata)
                .ThenInclude(mm => mm.Genres)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Movie).MovieMetadata)
                .ThenInclude(mm => mm.Guids)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as MusicVideo).MusicVideoMetadata)
                .ThenInclude(mm => mm.Artwork)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as MusicVideo).MusicVideoMetadata)
                .ThenInclude(mvm => mvm.Genres)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as MusicVideo).MusicVideoMetadata)
                .ThenInclude(mvm => mvm.Studios)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as MusicVideo).MusicVideoMetadata)
                .ThenInclude(mvm => mvm.Directors)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as MusicVideo).MusicVideoMetadata)
                .ThenInclude(mvm => mvm.Artists)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as MusicVideo).Artist)
                .ThenInclude(a => a.ArtistMetadata)
                .ThenInclude(am => am.Genres)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as OtherVideo).OtherVideoMetadata)
                .ThenInclude(vm => vm.Artwork)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as RemoteStream).RemoteStreamMetadata)
                .ThenInclude(vm => vm.Artwork)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Song).SongMetadata)
                .ThenInclude(vm => vm.Artwork)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Song).SongMetadata)
                .ThenInclude(sm => sm.Genres)
                .Include(p => p.Items)
                .ThenInclude(i => i.MediaItem)
                .ThenInclude(i => (i as Song).SongMetadata)
                .ThenInclude(sm => sm.Studios)
                .AsSplitQuery()
                .ToListAsync(cancellationToken);

            // apply playout offset to all items consistently
            foreach (Playout playout in playouts)
            {
                foreach (PlayoutItem item in playout.Items)
                {
                    item.Start += playoutOffset;
                    item.GuideStart += playoutOffset;
                    item.Finish += playoutOffset;
                    item.GuideFinish += playoutOffset;
                }
            }

            await using RecyclableMemoryStream ms = _recyclableMemoryStreamManager.GetStream();
            await using var xml = XmlWriter.Create(
                ms,
                new XmlWriterSettings { Async = true, ConformanceLevel = ConformanceLevel.Fragment });

            int daysToBuild = await _configElementRepository
                .GetValue<int>(ConfigElementKey.XmltvDaysToBuild, cancellationToken)
                .IfNoneAsync(2);

            DateTimeOffset finish = DateTimeOffset.UtcNow.AddDays(daysToBuild);

            foreach (Playout playout in playouts)
            {
                switch (playout.ScheduleKind)
                {
                    case PlayoutScheduleKind.Classic:
                    case PlayoutScheduleKind.Sequential:
                    case PlayoutScheduleKind.Scripted:
                        var floodSorted = playouts
                            .Collect(p => p.Items)
                            .OrderBy(pi => pi.Start)
                            .Filter(pi => pi.StartOffset <= finish)
                            .ToList();

                        await WritePlayoutXml(
                            request,
                            floodSorted,
                            templateContext,
                            movieTemplate,
                            episodeTemplate,
                            musicVideoTemplate,
                            songTemplate,
                            otherVideoTemplate,
                            remoteStreamTemplate,
                            minifier,
                            xml,
                            cancellationToken);
                        break;
                    case PlayoutScheduleKind.Block:
                        var blockSorted = playouts
                            .Collect(p => p.Items)
                            .OrderBy(pi => pi.Start)
                            .Filter(pi => pi.StartOffset <= finish)
                            .ToList();

                        await WriteBlockPlayoutXml(
                            request,
                            blockSorted,
                            templateContext,
                            movieTemplate,
                            episodeTemplate,
                            musicVideoTemplate,
                            songTemplate,
                            otherVideoTemplate,
                            remoteStreamTemplate,
                            minifier,
                            xml,
                            cancellationToken);
                        break;
                    case PlayoutScheduleKind.ExternalJson:
                        var externalJsonSorted = (await CollectExternalJsonItems(playout.ScheduleFile))
                            .Filter(pi => pi.StartOffset <= finish)
                            .ToList();

                        await WritePlayoutXml(
                            request,
                            externalJsonSorted,
                            templateContext,
                            movieTemplate,
                            episodeTemplate,
                            musicVideoTemplate,
                            songTemplate,
                            otherVideoTemplate,
                            remoteStreamTemplate,
                            minifier,
                            xml,
                            cancellationToken);
                        break;
                }
            }

            await xml.FlushAsync();

            string tempFile = Path.GetTempFileName();
            await File.WriteAllBytesAsync(tempFile, ms.ToArray(), cancellationToken);

            File.Move(tempFile, targetFile, true);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // do nothing
        }
    }

    private async Task WritePlayoutXml(
        RefreshChannelData request,
        List<PlayoutItem> sorted,
        XmlTemplateContext templateContext,
        Template movieTemplate,
        Template episodeTemplate,
        Template musicVideoTemplate,
        Template songTemplate,
        Template otherVideoTemplate,
        Template remoteStreamTemplate,
        XmlMinifier minifier,
        XmlWriter xml,
        CancellationToken cancellationToken)
    {
        XmltvTimeZone xmltvTimeZone = await _configElementRepository
            .GetValue<XmltvTimeZone>(ConfigElementKey.XmltvTimeZone, cancellationToken)
            .IfNoneAsync(XmltvTimeZone.Local);

        // skip all filler that isn't pre-roll
        var i = 0;
        while (i < sorted.Count && sorted[i].FillerKind != FillerKind.None &&
               sorted[i].FillerKind != FillerKind.PreRoll)
        {
            i++;
        }

        while (i < sorted.Count)
        {
            PlayoutItem startItem = sorted[i];
            int j = i;
            while (sorted[j].FillerKind != FillerKind.None && j + 1 < sorted.Count)
            {
                j++;
            }

            PlayoutItem displayItem = sorted[j];
            bool hasCustomTitle = !string.IsNullOrWhiteSpace(startItem.CustomTitle);

            int finishIndex = j;
            while (finishIndex + 1 < sorted.Count && (sorted[finishIndex + 1].GuideGroup == startItem.GuideGroup
                                                      || sorted[finishIndex + 1].FillerKind is FillerKind.GuideMode
                                                          or FillerKind.PostRoll or FillerKind.Tail
                                                          or FillerKind.Fallback or FillerKind.DecoDefault))
            {
                finishIndex++;
            }

            PlayoutItem finishItem = sorted[finishIndex];
            i = finishIndex;

            DateTimeOffset startTime = xmltvTimeZone switch
            {
                XmltvTimeZone.Utc => new DateTimeOffset(startItem.Start, TimeSpan.Zero),
                _ => startItem.StartOffset
            };

            DateTimeOffset stopTime = (xmltvTimeZone, displayItem.GuideFinishOffset.HasValue) switch
            {
                (XmltvTimeZone.Utc, true) => new DateTimeOffset(displayItem.GuideFinish!.Value, TimeSpan.Zero),
                (XmltvTimeZone.Utc, false) => new DateTimeOffset(finishItem.Finish, TimeSpan.Zero),
                (_, true) => displayItem.GuideFinishOffset!.Value,
                (_, false) => finishItem.FinishOffset
            };

            string start = startTime
                .ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
                .Replace(":", string.Empty);

            string stop = stopTime
                .ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
                .Replace(":", string.Empty);

            await WriteItemToXml(
                request,
                displayItem,
                start,
                stop,
                hasCustomTitle,
                templateContext,
                movieTemplate,
                episodeTemplate,
                musicVideoTemplate,
                songTemplate,
                otherVideoTemplate,
                remoteStreamTemplate,
                minifier,
                xml);

            i++;
        }
    }

    private async Task WriteBlockPlayoutXml(
        RefreshChannelData request,
        List<PlayoutItem> sorted,
        XmlTemplateContext templateContext,
        Template movieTemplate,
        Template episodeTemplate,
        Template musicVideoTemplate,
        Template songTemplate,
        Template otherVideoTemplate,
        Template remoteStreamTemplate,
        XmlMinifier minifier,
        XmlWriter xml,
        CancellationToken cancellationToken)
    {
        XmltvTimeZone xmltvTimeZone = await _configElementRepository
            .GetValue<XmltvTimeZone>(ConfigElementKey.XmltvTimeZone, cancellationToken)
            .IfNoneAsync(XmltvTimeZone.Local);

        XmltvBlockBehavior xmltvBlockBehavior = await _configElementRepository
            .GetValue<XmltvBlockBehavior>(ConfigElementKey.XmltvBlockBehavior, cancellationToken)
            .IfNoneAsync(XmltvBlockBehavior.SplitTimeEvenly);

        var groups = sorted.GroupBy(s => new { s.GuideStart, s.GuideFinish, s.GuideGroup });
        foreach (var group in groups)
        {
            var itemsToInclude = group.Filter(g => g.FillerKind is FillerKind.None).ToList();
            if (itemsToInclude.Count == 0)
            {
                continue;
            }

            switch (xmltvBlockBehavior)
            {
                case XmltvBlockBehavior.UseActualTimes:
                    foreach (PlayoutItem item in itemsToInclude)
                    {
                        DateTimeOffset actualStart = xmltvTimeZone switch
                        {
                            XmltvTimeZone.Utc => new DateTimeOffset(item.Start, TimeSpan.Zero),
                            _ => new DateTimeOffset(item.Start, TimeSpan.Zero).ToLocalTime()
                        };

                        DateTimeOffset actualFinish = xmltvTimeZone switch
                        {
                            XmltvTimeZone.Utc => new DateTimeOffset(item.Finish, TimeSpan.Zero),
                            _ => new DateTimeOffset(item.Finish, TimeSpan.Zero).ToLocalTime()
                        };

                        string start = actualStart.ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
                            .Replace(":", string.Empty);
                        string stop = actualFinish.ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
                            .Replace(":", string.Empty);

                        await WriteItemToXml(
                            request,
                            item,
                            start,
                            stop,
                            false,
                            templateContext,
                            movieTemplate,
                            episodeTemplate,
                            musicVideoTemplate,
                            songTemplate,
                            otherVideoTemplate,
                            remoteStreamTemplate,
                            minifier,
                            xml);
                    }
                    break;
                case XmltvBlockBehavior.SplitTimeEvenly:
                default:
                    DateTime groupStart = group.Key.GuideStart!.Value;
                    DateTime groupFinish = group.Key.GuideFinish!.Value;
                    TimeSpan groupDuration = groupFinish - groupStart;

                    TimeSpan perItem = groupDuration / itemsToInclude.Count;

                    DateTimeOffset currentStart = xmltvTimeZone switch
                    {
                        XmltvTimeZone.Utc => new DateTimeOffset(groupStart, TimeSpan.Zero),
                        _ => new DateTimeOffset(groupStart, TimeSpan.Zero).ToLocalTime()
                    };

                    DateTimeOffset currentFinish = currentStart + perItem;

                    foreach (PlayoutItem item in itemsToInclude)
                    {
                        string start = currentStart.ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
                            .Replace(":", string.Empty);
                        string stop = currentFinish.ToString("yyyyMMddHHmmss zzz", CultureInfo.InvariantCulture)
                            .Replace(":", string.Empty);

                        await WriteItemToXml(
                            request,
                            item,
                            start,
                            stop,
                            false,
                            templateContext,
                            movieTemplate,
                            episodeTemplate,
                            musicVideoTemplate,
                            songTemplate,
                            otherVideoTemplate,
                            remoteStreamTemplate,
                            minifier,
                            xml);

                        currentStart = currentFinish;
                        currentFinish += perItem;
                    }
                    break;
            }
        }
    }

    private static async Task WriteItemToXml(
        RefreshChannelData request,
        PlayoutItem displayItem,
        string start,
        string stop,
        bool hasCustomTitle,
        XmlTemplateContext templateContext,
        Template movieTemplate,
        Template episodeTemplate,
        Template musicVideoTemplate,
        Template songTemplate,
        Template otherVideoTemplate,
        Template remoteStreamTemplate,
        XmlMinifier minifier,
        XmlWriter xml)
    {
        string title = GetTitle(displayItem);
        string subtitle = GetSubtitle(displayItem);

        Option<string> maybeTemplateOutput = displayItem.MediaItem switch
        {
            Movie templateMovie => await ProcessMovieTemplate(
                request,
                templateMovie,
                start,
                stop,
                hasCustomTitle,
                displayItem,
                title,
                templateContext,
                movieTemplate),
            Episode templateEpisode => await ProcessEpisodeTemplate(
                request,
                templateEpisode,
                start,
                stop,
                hasCustomTitle,
                displayItem,
                title,
                subtitle,
                templateContext,
                episodeTemplate),
            MusicVideo templateMusicVideo => await ProcessMusicVideoTemplate(
                request,
                templateMusicVideo,
                start,
                stop,
                hasCustomTitle,
                displayItem,
                title,
                subtitle,
                templateContext,
                musicVideoTemplate),
            Song templateSong => await ProcessSongTemplate(
                request,
                templateSong,
                start,
                stop,
                hasCustomTitle,
                displayItem,
                title,
                subtitle,
                templateContext,
                songTemplate),
            OtherVideo templateOtherVideo => await ProcessOtherVideoTemplate(
                request,
                templateOtherVideo,
                start,
                stop,
                hasCustomTitle,
                displayItem,
                title,
                templateContext,
                otherVideoTemplate),
            RemoteStream templateRemoteStream => await ProcessRemoteStreamTemplate(
                request,
                templateRemoteStream,
                start,
                stop,
                hasCustomTitle,
                displayItem,
                title,
                templateContext,
                remoteStreamTemplate),
            _ => Option<string>.None
        };

        foreach (string templateOutput in maybeTemplateOutput)
        {
            MarkupMinificationResult minified = minifier.Minify(templateOutput);
            await xml.WriteRawAsync(minified.MinifiedContent);
        }
    }

    private static async Task<Option<string>> ProcessMovieTemplate(
        RefreshChannelData request,
        Movie templateMovie,
        string start,
        string stop,
        bool hasCustomTitle,
        PlayoutItem displayItem,
        string title,
        XmlTemplateContext templateContext,
        Template movieTemplate)
    {
        foreach (MovieMetadata metadata in templateMovie.MovieMetadata.HeadOrNone())
        {
            metadata.Genres ??= [];
            metadata.Guids ??= [];

            string poster = Optional(metadata.Artwork).Flatten()
                .Filter(a => a.ArtworkKind == ArtworkKind.Poster)
                .HeadOrNone()
                .Match(a => GetArtworkUrl(a, ArtworkKind.Poster), () => string.Empty);

            var data = new
            {
                ProgrammeStart = start,
                ProgrammeStop = stop,
                ChannelId = ChannelIdentifier.FromNumber(request.ChannelNumber),
                ChannelIdLegacy = ChannelIdentifier.LegacyFromNumber(request.ChannelNumber),
                request.ChannelNumber,
                HasCustomTitle = hasCustomTitle,
                displayItem.CustomTitle,
                MovieTitle = title,
                MovieHasPlot = !string.IsNullOrWhiteSpace(metadata.Plot),
                MoviePlot = metadata.Plot,
                MovieHasYear = metadata.Year.HasValue,
                MovieYear = metadata.Year,
                MovieGenres = metadata.Genres.Map(g => g.Name).OrderBy(n => n),
                MovieHasArtwork = !string.IsNullOrWhiteSpace(poster),
                MovieArtworkUrl = poster,
                MovieHasContentRating = !string.IsNullOrWhiteSpace(metadata.ContentRating),
                MovieContentRating = metadata.ContentRating,
                MovieGuids = metadata.Guids.Map(g => g.Guid)
            };

            var scriptObject = new ScriptObject();
            scriptObject.Import(data);
            templateContext.PushGlobal(scriptObject);

            return await movieTemplate.RenderAsync(templateContext);
        }

        return Option<string>.None;
    }

    private static async Task<Option<string>> ProcessEpisodeTemplate(
        RefreshChannelData request,
        Episode templateEpisode,
        string start,
        string stop,
        bool hasCustomTitle,
        PlayoutItem displayItem,
        string title,
        string subtitle,
        XmlTemplateContext templateContext,
        Template episodeTemplate)
    {
        foreach (EpisodeMetadata metadata in templateEpisode.EpisodeMetadata.HeadOrNone())
        {
            metadata.Genres ??= [];
            metadata.Guids ??= [];

            foreach (ShowMetadata showMetadata in Optional(
                         templateEpisode.Season?.Show?.ShowMetadata.HeadOrNone()).Flatten())
            {
                showMetadata.Genres ??= [];
                showMetadata.Guids ??= [];

                string artworkPath = GetPrioritizedArtworkPath(showMetadata);
                string thumbnailPath = GetPrioritizedArtworkPath(metadata);

                var data = new
                {
                    ProgrammeStart = start,
                    ProgrammeStop = stop,
                    ChannelId = ChannelIdentifier.FromNumber(request.ChannelNumber),
                    ChannelIdLegacy = ChannelIdentifier.LegacyFromNumber(request.ChannelNumber),
                    request.ChannelNumber,
                    HasCustomTitle = hasCustomTitle,
                    displayItem.CustomTitle,
                    ShowTitle = title,
                    EpisodeHasTitle = !string.IsNullOrWhiteSpace(subtitle),
                    EpisodeTitle = subtitle,
                    EpisodeHasPlot = !string.IsNullOrWhiteSpace(metadata.Plot),
                    EpisodePlot = metadata.Plot,
                    ShowHasYear = showMetadata.Year.HasValue,
                    ShowYear = showMetadata.Year,
                    ShowGenres = showMetadata.Genres.Map(g => g.Name).OrderBy(n => n),
                    EpisodeHasArtwork = !string.IsNullOrWhiteSpace(artworkPath),
                    EpisodeArtworkUrl = artworkPath,
                    EpisodeHasThumbnail = !string.IsNullOrWhiteSpace(thumbnailPath),
                    EpisodeThumbnailUrl = thumbnailPath,
                    SeasonNumber = templateEpisode.Season?.SeasonNumber ?? 0,
                    metadata.EpisodeNumber,
                    ShowHasContentRating = !string.IsNullOrWhiteSpace(showMetadata.ContentRating),
                    ShowContentRating = showMetadata.ContentRating,
                    ShowGuids = showMetadata.Guids.Map(g => g.Guid),
                    EpisodeGuids = metadata.Guids.Map(g => g.Guid)
                };

                var scriptObject = new ScriptObject();
                scriptObject.Import(data);
                templateContext.PushGlobal(scriptObject);

                return await episodeTemplate.RenderAsync(templateContext);
            }
        }

        return Option<string>.None;
    }

    private static async Task<Option<string>> ProcessMusicVideoTemplate(
        RefreshChannelData request,
        MusicVideo templateMusicVideo,
        string start,
        string stop,
        bool hasCustomTitle,
        PlayoutItem displayItem,
        string title,
        string subtitle,
        XmlTemplateContext templateContext,
        Template musicVideoTemplate)
    {
        foreach (MusicVideoMetadata metadata in templateMusicVideo.MusicVideoMetadata.HeadOrNone())
        {
            metadata.Genres ??= [];
            metadata.Artists ??= [];
            metadata.Studios ??= [];
            metadata.Directors ??= [];

            string artworkPath = GetPrioritizedArtworkPath(metadata);

            Option<ArtistMetadata> maybeMetadata =
                Optional(templateMusicVideo.Artist?.ArtistMetadata.HeadOrNone()).Flatten();

            var data = new
            {
                ProgrammeStart = start,
                ProgrammeStop = stop,
                ChannelId = ChannelIdentifier.FromNumber(request.ChannelNumber),
                ChannelIdLegacy = ChannelIdentifier.LegacyFromNumber(request.ChannelNumber),
                request.ChannelNumber,
                HasCustomTitle = hasCustomTitle,
                displayItem.CustomTitle,
                ArtistTitle = title,
                MusicVideoTitle = subtitle,
                MusicVideoHasPlot = !string.IsNullOrWhiteSpace(metadata.Plot),
                MusicVideoPlot = metadata.Plot,
                MusicVideoHasYear = metadata.Year.HasValue,
                MusicVideoYear = metadata.Year,
                MusicVideoGenres = metadata.Genres.Map(g => g.Name).OrderBy(n => n),
                ArtistGenres = maybeMetadata.SelectMany(m => m.Genres.Map(g => g.Name)).OrderBy(n => n),
                MusicVideoHasArtwork = !string.IsNullOrWhiteSpace(artworkPath),
                MusicVideoArtworkUrl = artworkPath,
                MusicVideoHasTrack = metadata.Track.HasValue,
                MusicVideoTrack = metadata.Track,
                MusicVideoHasAlbum = !string.IsNullOrWhiteSpace(metadata.Album),
                MusicVideoAlbum = metadata.Album,
                MusicVideoHasReleaseDate = metadata.ReleaseDate.HasValue,
                MusicVideoReleaseDate = metadata.ReleaseDate,
                MusicVideoAllArtists = metadata.Artists.Map(a => a.Name),
                MusicVideoStudios = metadata.Studios.Map(s => s.Name),
                MusicVideoDirectors = metadata.Directors.Map(d => d.Name)
            };

            var scriptObject = new ScriptObject();
            scriptObject.Import(data);
            templateContext.PushGlobal(scriptObject);

            return await musicVideoTemplate.RenderAsync(templateContext);
        }

        return Option<string>.None;
    }

    private static async Task<Option<string>> ProcessSongTemplate(
        RefreshChannelData request,
        Song templateSong,
        string start,
        string stop,
        bool hasCustomTitle,
        PlayoutItem displayItem,
        string title,
        string subtitle,
        XmlTemplateContext templateContext,
        Template songTemplate)
    {
        foreach (SongMetadata metadata in templateSong.SongMetadata.HeadOrNone())
        {
            metadata.Genres ??= [];
            metadata.Studios ??= [];

            string artworkPath = GetPrioritizedArtworkPath(metadata);

            var data = new
            {
                ProgrammeStart = start,
                ProgrammeStop = stop,
                ChannelId = ChannelIdentifier.FromNumber(request.ChannelNumber),
                ChannelIdLegacy = ChannelIdentifier.LegacyFromNumber(request.ChannelNumber),
                request.ChannelNumber,
                HasCustomTitle = hasCustomTitle,
                displayItem.CustomTitle,
                SongTitle = subtitle,
                SongArtists = metadata.Artists,
                SongAlbumArtists = metadata.AlbumArtists,
                SongHasYear = metadata.Year.HasValue,
                SongYear = metadata.Year,
                SongGenres = metadata.Genres.Map(g => g.Name).OrderBy(n => n),
                SongHasArtwork = !string.IsNullOrWhiteSpace(artworkPath),
                SongArtworkUrl = artworkPath,
                SongHasTrack = !string.IsNullOrWhiteSpace(metadata.Track),
                SongTrack = metadata.Track,
                SongHasComment = !string.IsNullOrWhiteSpace(metadata.Comment),
                SongComment = metadata.Comment,
                SongHasAlbum = !string.IsNullOrWhiteSpace(metadata.Album),
                SongAlbum = metadata.Album,
                SongHasReleaseDate = metadata.ReleaseDate.HasValue,
                SongReleaseDate = metadata.ReleaseDate,
                SongStudios = metadata.Studios.Map(s => s.Name)
            };

            var scriptObject = new ScriptObject();
            scriptObject.Import(data);
            templateContext.PushGlobal(scriptObject);

            return await songTemplate.RenderAsync(templateContext);
        }

        return Option<string>.None;
    }

    private static async Task<Option<string>> ProcessOtherVideoTemplate(
        RefreshChannelData request,
        OtherVideo templateOtherVideo,
        string start,
        string stop,
        bool hasCustomTitle,
        PlayoutItem displayItem,
        string title,
        XmlTemplateContext templateContext,
        Template otherVideoTemplate)
    {
        foreach (OtherVideoMetadata metadata in templateOtherVideo.OtherVideoMetadata.HeadOrNone())
        {
            metadata.Genres ??= [];
            metadata.Guids ??= [];

            string artworkPath = GetPrioritizedArtworkPath(metadata);

            var data = new
            {
                ProgrammeStart = start,
                ProgrammeStop = stop,
                ChannelId = ChannelIdentifier.FromNumber(request.ChannelNumber),
                ChannelIdLegacy = ChannelIdentifier.LegacyFromNumber(request.ChannelNumber),
                request.ChannelNumber,
                HasCustomTitle = hasCustomTitle,
                displayItem.CustomTitle,
                OtherVideoTitle = title,
                OtherVideoHasPlot = !string.IsNullOrWhiteSpace(metadata.Plot),
                OtherVideoPlot = metadata.Plot,
                OtherVideoHasYear = metadata.Year.HasValue,
                OtherVideoYear = metadata.Year,
                OtherVideoHasArtwork = !string.IsNullOrWhiteSpace(artworkPath),
                OtherVideoArtworkUrl = artworkPath,
                OtherVideoGenres = metadata.Genres.Map(g => g.Name).OrderBy(n => n),
                OtherVideoHasContentRating = !string.IsNullOrWhiteSpace(metadata.ContentRating),
                OtherVideoContentRating = metadata.ContentRating
            };

            var scriptObject = new ScriptObject();
            scriptObject.Import(data);
            templateContext.PushGlobal(scriptObject);

            return await otherVideoTemplate.RenderAsync(templateContext);
        }

        return Option<string>.None;
    }

    private static async Task<Option<string>> ProcessRemoteStreamTemplate(
        RefreshChannelData request,
        RemoteStream templateRemoteStream,
        string start,
        string stop,
        bool hasCustomTitle,
        PlayoutItem displayItem,
        string title,
        XmlTemplateContext templateContext,
        Template remoteStreamTemplate)
    {
        foreach (RemoteStreamMetadata metadata in templateRemoteStream.RemoteStreamMetadata.HeadOrNone())
        {
            metadata.Genres ??= [];
            metadata.Guids ??= [];

            string artworkPath = GetPrioritizedArtworkPath(metadata);

            var data = new
            {
                ProgrammeStart = start,
                ProgrammeStop = stop,
                ChannelId = ChannelIdentifier.FromNumber(request.ChannelNumber),
                ChannelIdLegacy = ChannelIdentifier.LegacyFromNumber(request.ChannelNumber),
                request.ChannelNumber,
                HasCustomTitle = hasCustomTitle,
                displayItem.CustomTitle,
                RemoteStreamTitle = title,
                RemoteStreamHasPlot = !string.IsNullOrWhiteSpace(metadata.Plot),
                RemoteStreamPlot = metadata.Plot,
                RemoteStreamHasYear = metadata.Year.HasValue,
                RemoteStreamYear = metadata.Year,
                RemoteStreamHasArtwork = !string.IsNullOrWhiteSpace(artworkPath),
                RemoteStreamArtworkUrl = artworkPath,
                RemoteStreamGenres = metadata.Genres.Map(g => g.Name).OrderBy(n => n),
                RemoteStreamHasContentRating = !string.IsNullOrWhiteSpace(metadata.ContentRating),
                RemoteStreamContentRating = metadata.ContentRating
            };

            var scriptObject = new ScriptObject();
            scriptObject.Import(data);
            templateContext.PushGlobal(scriptObject);

            return await remoteStreamTemplate.RenderAsync(templateContext);
        }

        return Option<string>.None;
    }

    private string GetMovieTemplateFileName()
    {
        string templateFileName = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ChannelGuideTemplatesFolder,
            "movie.sbntxt");

        // fail if file doesn't exist
        if (!_fileSystem.File.Exists(templateFileName))
        {
            _logger.LogError(
                "Unable to generate movie XMLTV fragment without template file {File}; please restart ErsatzTV",
                templateFileName);

            return null;
        }

        return templateFileName;
    }

    private string GetEpisodeTemplateFileName()
    {
        string templateFileName = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ChannelGuideTemplatesFolder,
            "episode.sbntxt");

        // fail if file doesn't exist
        if (!_fileSystem.File.Exists(templateFileName))
        {
            _logger.LogError(
                "Unable to generate episode XMLTV fragment without template file {File}; please restart ErsatzTV",
                templateFileName);

            return null;
        }

        return templateFileName;
    }

    private string GetMusicVideoTemplateFileName()
    {
        string templateFileName = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ChannelGuideTemplatesFolder,
            "musicVideo.sbntxt");

        // fail if file doesn't exist
        if (!_fileSystem.File.Exists(templateFileName))
        {
            _logger.LogError(
                "Unable to generate music video XMLTV fragment without template file {File}; please restart ErsatzTV",
                templateFileName);

            return null;
        }

        return templateFileName;
    }

    private string GetSongTemplateFileName()
    {
        string templateFileName = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ChannelGuideTemplatesFolder,
            "song.sbntxt");

        // fail if file doesn't exist
        if (!_fileSystem.File.Exists(templateFileName))
        {
            _logger.LogError(
                "Unable to generate song XMLTV fragment without template file {File}; please restart ErsatzTV",
                templateFileName);

            return null;
        }

        return templateFileName;
    }

    private string GetOtherVideoTemplateFileName()
    {
        string templateFileName = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ChannelGuideTemplatesFolder,
            "otherVideo.sbntxt");

        // fail if file doesn't exist
        if (!_fileSystem.File.Exists(templateFileName))
        {
            _logger.LogError(
                "Unable to generate other video XMLTV fragment without template file {File}; please restart ErsatzTV",
                templateFileName);

            return null;
        }

        return templateFileName;
    }

    private string GetRemoteStreamTemplateFileName()
    {
        string templateFileName = _localFileSystem.GetCustomOrDefaultFile(
            FileSystemLayout.ChannelGuideTemplatesFolder,
            "remoteStream.sbntxt");

        // fail if file doesn't exist
        if (!_fileSystem.File.Exists(templateFileName))
        {
            _logger.LogError(
                "Unable to generate remote stream XMLTV fragment without template file {File}; please restart ErsatzTV",
                templateFileName);

            return null;
        }

        return templateFileName;
    }

    private static string GetArtworkUrl(Artwork artwork, ArtworkKind artworkKind)
    {
        string artworkPath = artwork.Path;

        int height = artworkKind switch
        {
            ArtworkKind.Thumbnail => 220,
            _ => 440
        };

        if (artworkPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            artworkPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return artworkPath;
        }

        if (artworkPath.StartsWith("jellyfin://", StringComparison.OrdinalIgnoreCase))
        {
            artworkPath = JellyfinUrl.PlaceholderProxyForArtwork(artworkPath, artworkKind, height);
        }
        else if (artworkPath.StartsWith("emby://", StringComparison.OrdinalIgnoreCase))
        {
            artworkPath = EmbyUrl.PlaceholderProxyForArtwork(artworkPath, artworkKind, height);
        }
        else
        {
            string artworkFolder = artworkKind switch
            {
                ArtworkKind.Thumbnail => "thumbnails",
                _ => "posters"
            };

            artworkPath = $"{{RequestBase}}/iptv/artwork/{artworkFolder}/{artwork.Path}.jpg{{AccessTokenUri}}";
        }

        return artworkPath;
    }

    private static string GetTitle(PlayoutItem playoutItem)
    {
        if (!string.IsNullOrWhiteSpace(playoutItem.CustomTitle))
        {
            return playoutItem.CustomTitle;
        }

        return playoutItem.MediaItem switch
        {
            Movie m => m.MovieMetadata.HeadOrNone().Map(mm => mm.Title ?? string.Empty)
                .IfNone("[unknown movie]"),
            Episode e => e.Season.Show.ShowMetadata.HeadOrNone().Map(em => em.Title ?? string.Empty)
                .IfNone("[unknown show]"),
            MusicVideo mv => mv.Artist.ArtistMetadata.HeadOrNone().Map(am => am.Title ?? string.Empty)
                .IfNone("[unknown artist]"),
            OtherVideo ov => ov.OtherVideoMetadata.HeadOrNone().Map(vm => vm.Title ?? string.Empty)
                .IfNone("[unknown video]"),
            RemoteStream rs => rs.RemoteStreamMetadata.HeadOrNone().Map(vm => vm.Title ?? string.Empty)
                .IfNone("[unknown remote stream]"),
            _ => "[unknown]"
        };
    }

    private static string GetSubtitle(PlayoutItem playoutItem)
    {
        if (!string.IsNullOrWhiteSpace(playoutItem.CustomTitle))
        {
            return string.Empty;
        }

        return playoutItem.MediaItem switch
        {
            Episode e => e.EpisodeMetadata.HeadOrNone().Match(
                em => em.Title ?? string.Empty,
                () => string.Empty),
            MusicVideo mv => mv.MusicVideoMetadata.HeadOrNone().Match(
                mvm => mvm.Title ?? string.Empty,
                () => string.Empty),
            Song s => s.SongMetadata.HeadOrNone().Match(
                mvm => mvm.Title ?? string.Empty,
                () => string.Empty),
            _ => string.Empty
        };
    }

    private static string GetPrioritizedArtworkPath(Metadata metadata)
    {
        Option<string> maybeArtwork = Optional(metadata.Artwork).Flatten()
            .Filter(a => a.ArtworkKind == ArtworkKind.Poster)
            .HeadOrNone()
            .Map(a => GetArtworkUrl(a, ArtworkKind.Poster));

        if (maybeArtwork.IsNone)
        {
            maybeArtwork = Optional(metadata.Artwork).Flatten()
                .Filter(a => a.ArtworkKind == ArtworkKind.Thumbnail)
                .HeadOrNone()
                .Map(a => GetArtworkUrl(a, ArtworkKind.Thumbnail));
        }

        return maybeArtwork.IfNone(string.Empty);
    }

    private async Task<List<PlayoutItem>> CollectExternalJsonItems(string path)
    {
        var result = new List<PlayoutItem>();

        if (_fileSystem.File.Exists(path))
        {
            Option<ExternalJsonChannel> maybeChannel = JsonConvert.DeserializeObject<ExternalJsonChannel>(
                await File.ReadAllTextAsync(path));

            // must deserialize channel from json
            foreach (ExternalJsonChannel channel in maybeChannel)
            {
                if (string.IsNullOrWhiteSpace(channel.StartTime))
                {
                    _logger.LogError(
                        "External json channel in file {File} has no start time; unable to refresh channel data",
                        path);

                    throw new InvalidOperationException(
                        $"External json channel in file {path} has no start time");
                }

                DateTimeOffset startTime = DateTimeOffset.Parse(
                    channel.StartTime,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal).ToLocalTime();

                for (var i = 0; i < channel.Programs.Length; i++)
                {
                    ExternalJsonProgram program = channel.Programs[i];
                    int milliseconds = program.Duration;
                    DateTimeOffset nextStart = startTime + TimeSpan.FromMilliseconds(milliseconds);
                    if (program.Duration >= channel.GuideMinimumDurationSeconds * 1000)
                    {
                        result.Add(BuildPlayoutItem(startTime, program, i));
                    }

                    startTime = nextStart;
                }
            }
        }

        return result;
    }

    private static PlayoutItem BuildPlayoutItem(DateTimeOffset startTime, ExternalJsonProgram program, int count)
    {
        MediaItem mediaItem = program.Type switch
        {
            "episode" => BuildEpisode(program),
            _ => BuildMovie(program)
        };

        return new PlayoutItem
        {
            Start = startTime.UtcDateTime,
            Finish = startTime.AddMilliseconds(program.Duration).UtcDateTime,
            FillerKind = FillerKind.None,
            ChapterTitle = null,
            GuideFinish = null,
            GuideGroup = count,
            CustomTitle = null,
            InPoint = TimeSpan.Zero,
            OutPoint = TimeSpan.FromMilliseconds(program.Duration),
            MediaItem = mediaItem
        };
    }

    private static Episode BuildEpisode(ExternalJsonProgram program)
    {
        var artwork = new List<Artwork>();
        if (!string.IsNullOrWhiteSpace(program.Icon))
        {
            artwork.Add(
                new Artwork
                {
                    ArtworkKind = ArtworkKind.Thumbnail,
                    Path = program.Icon,
                    SourcePath = program.Icon
                });
        }

        return new Episode
        {
            MediaVersions =
            [
                new MediaVersion
                {
                    Duration = TimeSpan.FromMilliseconds(program.Duration)
                }
            ],
            EpisodeMetadata =
            [
                new EpisodeMetadata
                {
                    EpisodeNumber = program.Episode,
                    Title = program.Title
                }
            ],
            Season = new Season
            {
                SeasonNumber = program.Season,
                Show = new Show
                {
                    ShowMetadata =
                    [
                        new ShowMetadata
                        {
                            Title = program.ShowTitle,
                            Artwork = artwork
                        }
                    ]
                }
            }
        };
    }

    private static Movie BuildMovie(ExternalJsonProgram program)
    {
        var artwork = new List<Artwork>();
        if (!string.IsNullOrWhiteSpace(program.Icon))
        {
            artwork.Add(
                new Artwork
                {
                    ArtworkKind = ArtworkKind.Poster,
                    Path = program.Icon,
                    SourcePath = program.Icon
                });
        }

        return new Movie
        {
            MediaVersions =
            [
                new MediaVersion
                {
                    Duration = TimeSpan.FromMilliseconds(program.Duration)
                }
            ],
            MovieMetadata =
            [
                new MovieMetadata
                {
                    Title = program.Title,
                    Year = program.Year,
                    Artwork = artwork
                }
            ]
        };
    }
}

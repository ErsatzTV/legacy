using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.MediaServer;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Infrastructure;
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public abstract class MediaServerTelevisionLibraryScanner<TConnectionParameters, TLibrary, TShow, TSeason, TEpisode,
    TEtag>
    where TConnectionParameters : MediaServerConnectionParameters
    where TLibrary : Library
    where TShow : Show
    where TSeason : Season
    where TEpisode : Episode
    where TEtag : MediaServerItemEtag
{
    private readonly ILocalChaptersProvider _localChaptersProvider;
    private readonly IScannerProxy _scannerProxy;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly IMetadataRepository _metadataRepository;

    protected MediaServerTelevisionLibraryScanner(
        IScannerProxy scannerProxy,
        IFileSystem fileSystem,
        ILocalChaptersProvider localChaptersProvider,
        IMetadataRepository metadataRepository,
        ILogger logger)
    {
        _scannerProxy = scannerProxy;
        _fileSystem = fileSystem;
        _localChaptersProvider = localChaptersProvider;
        _metadataRepository = metadataRepository;
        _logger = logger;
    }

    protected virtual bool ServerSupportsRemoteStreaming => false;
    protected virtual bool ServerReturnsStatisticsWithMetadata => false;

    protected async Task<Either<BaseError, Unit>> ScanLibrary(
        IMediaServerTelevisionRepository<TLibrary, TShow, TSeason, TEpisode, TEtag> televisionRepository,
        TConnectionParameters connectionParameters,
        TLibrary library,
        Func<TEpisode, string> getLocalPath,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ScanLibrary(
                televisionRepository,
                connectionParameters,
                library,
                getLocalPath,
                GetShowLibraryItems(connectionParameters, library),
                deepScan,
                cancellationToken);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return new ScanCanceled();
        }
    }

    protected abstract IAsyncEnumerable<Tuple<TShow, int>> GetShowLibraryItems(
        TConnectionParameters connectionParameters,
        TLibrary library);

    protected abstract string MediaServerItemId(TShow show);
    protected abstract string MediaServerItemId(TSeason season);
    protected abstract string MediaServerItemId(TEpisode episode);
    protected abstract string MediaServerEtag(TShow show);
    protected abstract string MediaServerEtag(TSeason season);
    protected abstract string MediaServerEtag(TEpisode episode);

    protected async Task<Either<BaseError, Unit>> InternalScanLibrary(
        IMediaServerTelevisionRepository<TLibrary, TShow, TSeason, TEpisode, TEtag> televisionRepository,
        TConnectionParameters connectionParameters,
        TLibrary library,
        Func<TEpisode, string> getLocalPath,
        IAsyncEnumerable<Tuple<TShow, int>> showEntries,
        bool deepScan,
        bool cleanupFileNotFoundItems,
        CancellationToken cancellationToken)
    {
        var incomingItemIds = new List<string>();
        List<TEtag> existingShows = await televisionRepository.GetExistingShows(library, cancellationToken);

        await foreach ((TShow incoming, int totalShowCount) in showEntries.WithCancellation(cancellationToken))
        {
            using (ScanProfiler.Measure("Scan Show"))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                incomingItemIds.Add(MediaServerItemId(incoming));

                decimal percentCompletion = Math.Clamp((decimal)incomingItemIds.Count / totalShowCount, 0, 1);
                if (!await _scannerProxy.UpdateProgress(percentCompletion, cancellationToken))
                {
                    return new ScanCanceled();
                }

                Either<BaseError, MediaItemScanResult<TShow>> maybeShow = await televisionRepository
                    .GetOrAdd(library, incoming, cancellationToken)
                    .BindT(existing => UpdateMetadata(connectionParameters, library, existing, incoming, deepScan));

                if (maybeShow.IsLeft)
                {
                    foreach (BaseError error in maybeShow.LeftToSeq())
                    {
                        _logger.LogWarning(
                            "Error processing show {Title}: {Error}",
                            incoming.ShowMetadata.Head().Title,
                            error.Value);
                    }

                    continue;
                }

                foreach (MediaItemScanResult<TShow> result in maybeShow.RightToSeq())
                {
                    Either<BaseError, Unit> scanResult = await ScanSeasons(
                        televisionRepository,
                        library,
                        getLocalPath,
                        result.Item,
                        result.IsUpdated,
                        connectionParameters,
                        GetSeasonLibraryItems(library, connectionParameters, result.Item),
                        deepScan,
                        cancellationToken);

                    foreach (ScanCanceled error in scanResult.LeftToSeq().OfType<ScanCanceled>())
                    {
                        return error;
                    }

                    await televisionRepository.SetEtag(result.Item, MediaServerEtag(incoming), cancellationToken);

                    Option<int> flagResult = await televisionRepository.FlagNormal(
                        library,
                        result.Item,
                        cancellationToken);
                    if (flagResult.IsSome)
                    {
                        result.IsUpdated = true;
                    }

                    if (result.IsAdded || result.IsUpdated)
                    {
                        if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                        {
                            _logger.LogWarning("Failed to reindex media items from scanner process");
                        }
                    }
                }
            }

            ScanProfiler.LogStatistics(s => _logger.LogInformation("{Profile}", s));
        }

        if (cleanupFileNotFoundItems)
        {
            // trash shows that are no longer present on the media server
            var fileNotFoundItemIds = existingShows.Map(s => s.MediaServerItemId).Except(incomingItemIds).ToList();
            List<int> ids = await televisionRepository.FlagFileNotFoundShows(library, fileNotFoundItemIds, cancellationToken);
            if (!await _scannerProxy.ReindexMediaItems(ids.ToArray(), cancellationToken))
            {
                _logger.LogWarning("Failed to reindex media items from scanner process");
            }
        }

        return Unit.Default;
    }

    protected async Task<Either<BaseError, Unit>> ScanLibrary(
        IMediaServerTelevisionRepository<TLibrary, TShow, TSeason, TEpisode, TEtag> televisionRepository,
        TConnectionParameters connectionParameters,
        TLibrary library,
        Func<TEpisode, string> getLocalPath,
        IAsyncEnumerable<Tuple<TShow, int>> showEntries,
        bool deepScan,
        CancellationToken cancellationToken) =>
        await InternalScanLibrary(
            televisionRepository,
            connectionParameters,
            library,
            getLocalPath,
            showEntries,
            deepScan,
            true,
            cancellationToken);

    protected async Task<Either<BaseError, Unit>> ScanLibraryWithoutCleanup(
        IMediaServerTelevisionRepository<TLibrary, TShow, TSeason, TEpisode, TEtag> televisionRepository,
        TConnectionParameters connectionParameters,
        TLibrary library,
        Func<TEpisode, string> getLocalPath,
        IAsyncEnumerable<Tuple<TShow, int>> showEntries,
        bool deepScan,
        CancellationToken cancellationToken) =>
        await InternalScanLibrary(
            televisionRepository,
            connectionParameters,
            library,
            getLocalPath,
            showEntries,
            deepScan,
            false,
            cancellationToken);

    protected abstract IAsyncEnumerable<Tuple<TSeason, int>> GetSeasonLibraryItems(
        TLibrary library,
        TConnectionParameters connectionParameters,
        TShow show);

    protected abstract IAsyncEnumerable<Tuple<TEpisode, int>> GetEpisodeLibraryItems(
        TLibrary library,
        TConnectionParameters connectionParameters,
        TShow show,
        TSeason season,
        bool isNewSeason);

    protected abstract Task<Option<ShowMetadata>> GetFullMetadata(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TShow> result,
        TShow incoming,
        bool deepScan);

    protected abstract Task<Option<SeasonMetadata>> GetFullMetadata(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TSeason> result,
        TSeason incoming,
        bool deepScan);

    protected abstract Task<Option<EpisodeMetadata>> GetFullMetadata(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TEpisode> result,
        TEpisode incoming,
        bool deepScan);

    protected virtual Task<Option<MediaVersion>> GetMediaServerStatistics(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TEpisode> result,
        TEpisode incoming) => Task.FromResult(Option<MediaVersion>.None);

    protected abstract Task<Option<Tuple<EpisodeMetadata, MediaVersion>>> GetFullMetadataAndStatistics(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TEpisode> result,
        TEpisode incoming);

    protected abstract Task<Either<BaseError, MediaItemScanResult<TShow>>> UpdateMetadata(
        MediaItemScanResult<TShow> result,
        ShowMetadata fullMetadata);

    protected abstract Task<Either<BaseError, MediaItemScanResult<TSeason>>> UpdateMetadata(
        MediaItemScanResult<TSeason> result,
        SeasonMetadata fullMetadata);

    protected abstract Task<Either<BaseError, MediaItemScanResult<TEpisode>>> UpdateMetadata(
        MediaItemScanResult<TEpisode> result,
        EpisodeMetadata fullMetadata,
        CancellationToken cancellationToken);

    private async Task<Either<BaseError, Unit>> ScanSeasons(
        IMediaServerTelevisionRepository<TLibrary, TShow, TSeason, TEpisode, TEtag> televisionRepository,
        TLibrary library,
        Func<TEpisode, string> getLocalPath,
        TShow show,
        bool showIsUpdated,
        TConnectionParameters connectionParameters,
        IAsyncEnumerable<Tuple<TSeason, int>> seasonEntries,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        var incomingItemIds = new List<string>();
        List<TEtag> existingSeasons = await televisionRepository.GetExistingSeasons(library, show, cancellationToken);

        await foreach ((TSeason incoming, int _) in seasonEntries.WithCancellation(cancellationToken))
        {
            using (ScanProfiler.Measure("Scan Season"))
            {
                incoming.ShowId = show.Id;

                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                incomingItemIds.Add(MediaServerItemId(incoming));

                Either<BaseError, MediaItemScanResult<TSeason>> maybeSeason = await televisionRepository
                    .GetOrAdd(library, incoming, cancellationToken)
                    .BindT(existing => UpdateMetadata(connectionParameters, library, existing, incoming, deepScan));

                if (maybeSeason.IsLeft)
                {
                    foreach (BaseError error in maybeSeason.LeftToSeq())
                    {
                        _logger.LogWarning(
                            "Error processing show {Title} season {SeasonNumber}: {Error}",
                            show.ShowMetadata.Head().Title,
                            incoming.SeasonNumber,
                            error.Value);
                    }

                    continue;
                }

                foreach (MediaItemScanResult<TSeason> result in maybeSeason.RightToSeq())
                {
                    Either<BaseError, Unit> scanResult = await ScanEpisodes(
                        televisionRepository,
                        library,
                        getLocalPath,
                        show,
                        showIsUpdated,
                        result.Item,
                        connectionParameters,
                        GetEpisodeLibraryItems(library, connectionParameters, show, result.Item, result.IsAdded),
                        deepScan,
                        cancellationToken);

                    foreach (ScanCanceled error in scanResult.LeftToSeq().OfType<ScanCanceled>())
                    {
                        return error;
                    }

                    await televisionRepository.SetEtag(result.Item, MediaServerEtag(incoming), cancellationToken);

                    Option<int> flagResult = await televisionRepository.FlagNormal(
                        library,
                        result.Item,
                        cancellationToken);
                    if (flagResult.IsSome)
                    {
                        result.IsUpdated = true;
                    }

                    result.Item.Show = show;

                    if (result.IsAdded || result.IsUpdated || showIsUpdated)
                    {
                        if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                        {
                            _logger.LogWarning("Failed to reindex media items from scanner process");
                        }
                    }
                }
            }
        }

        // trash seasons that are no longer present on the media server
        var fileNotFoundItemIds = existingSeasons.Map(s => s.MediaServerItemId).Except(incomingItemIds).ToList();
        List<int> ids = await televisionRepository.FlagFileNotFoundSeasons(library, fileNotFoundItemIds, cancellationToken);
        if (!await _scannerProxy.ReindexMediaItems(ids.ToArray(), cancellationToken))
        {
            _logger.LogWarning("Failed to reindex media items from scanner process");
        }

        return Unit.Default;
    }

    private async Task<Either<BaseError, Unit>> ScanEpisodes(
        IMediaServerTelevisionRepository<TLibrary, TShow, TSeason, TEpisode, TEtag> televisionRepository,
        TLibrary library,
        Func<TEpisode, string> getLocalPath,
        TShow show,
        bool showIsUpdated,
        TSeason season,
        TConnectionParameters connectionParameters,
        IAsyncEnumerable<Tuple<TEpisode, int>> episodeEntries,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        var incomingItemIds = new List<string>();
        List<TEtag> existingEpisodes = await televisionRepository.GetExistingEpisodes(library, season, cancellationToken);

        await foreach ((TEpisode incoming, int _) in episodeEntries.WithCancellation(cancellationToken))
        {
            using (ScanProfiler.Measure("Scan Episode"))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                incomingItemIds.Add(MediaServerItemId(incoming));

                string localPath = getLocalPath(incoming);
                if (!await ShouldScanItem(
                        televisionRepository,
                        library,
                        show,
                        season,
                        existingEpisodes,
                        incoming,
                        localPath,
                        deepScan,
                        cancellationToken))
                {
                    continue;
                }

                incoming.SeasonId = season.Id;

                Either<BaseError, MediaItemScanResult<TEpisode>> maybeEpisode;

                if (ServerReturnsStatisticsWithMetadata)
                {
                    maybeEpisode = await televisionRepository
                        .GetOrAdd(library, incoming, deepScan, cancellationToken)
                        .MapT(result =>
                        {
                            result.LocalPath = localPath;
                            return result;
                        })
                        .BindT(existing => UpdateMetadataAndStatistics(
                            connectionParameters,
                            library,
                            existing,
                            incoming,
                            deepScan,
                            cancellationToken))
                        .BindT(existing => UpdateChapters(existing, cancellationToken));
                }
                else
                {
                    maybeEpisode = await televisionRepository
                        .GetOrAdd(library, incoming, deepScan, cancellationToken)
                        .MapT(result =>
                        {
                            result.LocalPath = localPath;
                            return result;
                        })
                        .BindT(existing => UpdateMetadata(
                            connectionParameters,
                            library,
                            existing,
                            incoming,
                            deepScan,
                            None,
                            cancellationToken))
                        .BindT(existing => UpdateStatistics(
                            connectionParameters,
                            library,
                            existing,
                            incoming,
                            deepScan,
                            None))
                        .BindT(existing => UpdateSubtitles(existing, incoming, deepScan, cancellationToken))
                        .BindT(existing => UpdateChapters(existing, cancellationToken));
                }

                if (maybeEpisode.IsLeft)
                {
                    foreach (BaseError error in maybeEpisode.LeftToSeq())
                    {
                        _logger.LogWarning(
                            "Error processing episode {Title} s{SeasonNumber:00}e{EpisodeNumber:00}: {Error}",
                            show.ShowMetadata.Head().Title,
                            season.SeasonNumber,
                            incoming.EpisodeMetadata.Head().EpisodeNumber,
                            error.Value);
                    }

                    continue;
                }

                foreach (MediaItemScanResult<TEpisode> result in maybeEpisode.RightToSeq())
                {
                    await televisionRepository.SetEtag(result.Item, MediaServerEtag(incoming), cancellationToken);

                    if (_fileSystem.File.Exists(result.LocalPath))
                    {
                        Option<int> flagResult = await televisionRepository.FlagNormal(
                            library,
                            result.Item,
                            cancellationToken);
                        if (flagResult.IsSome)
                        {
                            result.IsUpdated = true;
                        }
                    }
                    else if (ServerSupportsRemoteStreaming)
                    {
                        Option<int> flagResult = await televisionRepository.FlagRemoteOnly(
                            library,
                            result.Item,
                            cancellationToken);
                        if (flagResult.IsSome)
                        {
                            result.IsUpdated = true;
                        }
                    }
                    else
                    {
                        Option<int> flagResult = await televisionRepository.FlagUnavailable(
                            library,
                            result.Item,
                            cancellationToken);
                        if (flagResult.IsSome)
                        {
                            result.IsUpdated = true;
                        }
                    }

                    if (result.IsAdded || result.IsUpdated || showIsUpdated)
                    {
                        if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                        {
                            _logger.LogWarning("Failed to reindex media items from scanner process");
                        }

                    }
                }
            }
        }

        // trash episodes that are no longer present on the media server
        var fileNotFoundItemIds = existingEpisodes.Map(m => m.MediaServerItemId).Except(incomingItemIds).ToList();
        List<int> ids = await televisionRepository.FlagFileNotFoundEpisodes(library, fileNotFoundItemIds, cancellationToken);
        if (!await _scannerProxy.ReindexMediaItems(ids.ToArray(), cancellationToken))
        {
            _logger.LogWarning("Failed to reindex media items from scanner process");
        }

        return Unit.Default;
    }

    private async Task<bool> ShouldScanItem(
        IMediaServerTelevisionRepository<TLibrary, TShow, TSeason, TEpisode, TEtag> televisionRepository,
        TLibrary library,
        Show show,
        Season season,
        List<TEtag> existingEpisodes,
        TEpisode incoming,
        string localPath,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        // deep scan will always pull every episode
        if (deepScan)
        {
            return true;
        }

        Option<TEtag> maybeExisting = existingEpisodes.Find(m => m.MediaServerItemId == MediaServerItemId(incoming));
        string existingEtag = await maybeExisting.Map(e => e.Etag ?? string.Empty).IfNoneAsync(string.Empty);
        MediaItemState existingState = await maybeExisting.Map(e => e.State).IfNoneAsync(MediaItemState.Normal);

        if (existingState is MediaItemState.Unavailable or MediaItemState.FileNotFound &&
            existingEtag == MediaServerEtag(incoming))
        {
            // skip scanning unavailable/file not found items that are unchanged and still don't exist locally
            if (!_fileSystem.File.Exists(localPath) && !ServerSupportsRemoteStreaming)
            {
                return false;
            }
        }
        else if (existingEtag == MediaServerEtag(incoming))
        {
            // item is unchanged, but file does not exist
            // don't scan, but mark as unavailable
            if (!_fileSystem.File.Exists(localPath))
            {
                if (ServerSupportsRemoteStreaming)
                {
                    if (existingState is not MediaItemState.RemoteOnly)
                    {
                        foreach (int id in await televisionRepository.FlagRemoteOnly(library, incoming, cancellationToken))
                        {
                            if (!await _scannerProxy.ReindexMediaItems([id], cancellationToken))
                            {
                                _logger.LogWarning("Failed to reindex media items from scanner process");
                            }
                        }
                    }
                }
                else
                {
                    if (existingState is not MediaItemState.Unavailable)
                    {
                        foreach (int id in await televisionRepository.FlagUnavailable(library, incoming, cancellationToken))
                        {
                            if (!await _scannerProxy.ReindexMediaItems([id], cancellationToken))
                            {
                                _logger.LogWarning("Failed to reindex media items from scanner process");
                            }
                        }
                    }
                }
            }

            return false;
        }

        if (maybeExisting.IsNone)
        {
            _logger.LogDebug(
                "INSERT: new episode {Show} s{SeasonNumber:00}e{EpisodeNumber:00}",
                show.ShowMetadata.Head().Title,
                season.SeasonNumber,
                incoming.EpisodeMetadata.Head().EpisodeNumber);
        }
        else
        {
            _logger.LogDebug(
                "UPDATE: Etag has changed for episode {Show} s{SeasonNumber:00}e{EpisodeNumber:00}",
                show.ShowMetadata.Head().Title,
                season.SeasonNumber,
                incoming.EpisodeMetadata.Head().EpisodeNumber);
        }

        return true;
    }

    private async Task<Either<BaseError, MediaItemScanResult<TShow>>> UpdateMetadata(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TShow> result,
        TShow incoming,
        bool deepScan)
    {
        using (ScanProfiler.Measure("Update Show Metadata"))
        {
            foreach (ShowMetadata fullMetadata in await GetFullMetadata(
                         connectionParameters,
                         library,
                         result,
                         incoming,
                         deepScan))
            {
                // TODO: move some of this code into this scanner
                // will have to merge JF, Emby, Plex logic
                return await UpdateMetadata(result, fullMetadata);
            }
        }

        return result;
    }

    private async Task<Either<BaseError, MediaItemScanResult<TSeason>>> UpdateMetadata(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TSeason> result,
        TSeason incoming,
        bool deepScan)
    {
        using (ScanProfiler.Measure("Update Season Metadata"))
        {
            foreach (SeasonMetadata fullMetadata in await GetFullMetadata(
                         connectionParameters,
                         library,
                         result,
                         incoming,
                         deepScan))
            {
                // TODO: move some of this code into this scanner
                // will have to merge JF, Emby, Plex logic
                return await UpdateMetadata(result, fullMetadata);
            }
        }

        return result;
    }

    private async Task<Either<BaseError, MediaItemScanResult<TEpisode>>> UpdateMetadataAndStatistics(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TEpisode> result,
        TEpisode incoming,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        Option<Tuple<EpisodeMetadata, MediaVersion>> maybeMetadataAndStatistics = await GetFullMetadataAndStatistics(
            connectionParameters,
            library,
            result,
            incoming);

        foreach ((EpisodeMetadata fullMetadata, MediaVersion mediaVersion) in maybeMetadataAndStatistics)
        {
            Either<BaseError, MediaItemScanResult<TEpisode>> metadataResult = await UpdateMetadata(
                connectionParameters,
                library,
                result,
                incoming,
                deepScan,
                fullMetadata,
                cancellationToken);

            foreach (BaseError error in metadataResult.LeftToSeq())
            {
                return error;
            }

            foreach (MediaItemScanResult<TEpisode> r in metadataResult.RightToSeq())
            {
                result = r;
            }

            Either<BaseError, MediaItemScanResult<TEpisode>> statisticsResult = await UpdateStatistics(
                connectionParameters,
                library,
                result,
                incoming,
                deepScan,
                mediaVersion);

            foreach (BaseError error in statisticsResult.LeftToSeq())
            {
                return error;
            }

            foreach (MediaItemScanResult<TEpisode> r in metadataResult.RightToSeq())
            {
                result = r;
            }
        }

        return result;
    }

    private async Task<Either<BaseError, MediaItemScanResult<TEpisode>>> UpdateMetadata(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TEpisode> result,
        TEpisode incoming,
        bool deepScan,
        Option<EpisodeMetadata> maybeFullMetadata,
        CancellationToken cancellationToken)
    {
        using (ScanProfiler.Measure("Update Episode Metadata"))
        {
            if (maybeFullMetadata.IsNone)
            {
                maybeFullMetadata = await GetFullMetadata(connectionParameters, library, result, incoming, deepScan);
            }

            foreach (EpisodeMetadata fullMetadata in maybeFullMetadata)
            {
                // TODO: move some of this code into this scanner
                // will have to merge JF, Emby, Plex logic
                return await UpdateMetadata(result, fullMetadata, cancellationToken);
            }

            return result;
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<TEpisode>>> UpdateStatistics(
        TConnectionParameters connectionParameters,
        TLibrary library,
        MediaItemScanResult<TEpisode> result,
        TEpisode incoming,
        bool deepScan,
        Option<MediaVersion> maybeMediaVersion)
    {
        using (ScanProfiler.Measure("Update Episode Stats"))
        {
            TEpisode existing = result.Item;

            if (deepScan || result.IsAdded || MediaServerEtag(existing) != MediaServerEtag(incoming) ||
                existing.MediaVersions.Head().Streams.Count == 0)
            {
                // if (maybeMediaVersion.IsNone && _fileSystem.File.Exists(result.LocalPath))
                // {
                //     _logger.LogDebug("Refreshing {Attribute} for {Path}", "Statistics", result.LocalPath);
                //     Either<BaseError, bool> refreshResult =
                //         await _localStatisticsProvider.RefreshStatistics(
                //             ffmpegPath,
                //             ffprobePath,
                //             existing,
                //             result.LocalPath);
                //
                //     foreach (BaseError error in refreshResult.LeftToSeq())
                //     {
                //         _logger.LogWarning(
                //             "Unable to refresh {Attribute} for media item {Path}. Error: {Error}",
                //             "Statistics",
                //             result.LocalPath,
                //             error.Value);
                //     }
                //
                //     foreach (bool _ in refreshResult.RightToSeq())
                //     {
                //         result.IsUpdated = true;
                //     }
                // }
                // else
                // {
                if (maybeMediaVersion.IsNone)
                {
                    maybeMediaVersion = await GetMediaServerStatistics(
                        connectionParameters,
                        library,
                        result,
                        incoming);
                }

                foreach (MediaVersion mediaVersion in maybeMediaVersion)
                {
                    if (await _metadataRepository.UpdateStatistics(result.Item, mediaVersion))
                    {
                        result.IsUpdated = true;
                    }
                }
                // }
            }

            return result;
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<TEpisode>>> UpdateSubtitles(
        MediaItemScanResult<TEpisode> existing,
        TEpisode incoming,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        try
        {
            if (deepScan || existing.IsAdded || MediaServerEtag(existing.Item) != MediaServerEtag(incoming))
            {
                using (ScanProfiler.Measure("Update Episode Subtitles"))
                {
                    MediaVersion version = existing.Item.GetHeadVersion();
                    Option<EpisodeMetadata> maybeMetadata = existing.Item.EpisodeMetadata.HeadOrNone();
                    foreach (EpisodeMetadata metadata in maybeMetadata)
                    {
                        List<Subtitle> subtitles = version.Streams
                            .Filter(s =>
                                s.MediaStreamKind is MediaStreamKind.Subtitle or MediaStreamKind.ExternalSubtitle)
                            .Map(Subtitle.FromMediaStream)
                            .ToList();

                        if (await _metadataRepository.UpdateSubtitles(
                                metadata,
                                subtitles,
                                SidecarSubtitleIdentity.StreamIndex,
                                cancellationToken))
                        {
                            return existing;
                        }
                    }

                    return BaseError.New("Failed to update media server subtitles");
                }
            }

            return existing;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<TEpisode>>> UpdateChapters(
        MediaItemScanResult<TEpisode> existing,
        CancellationToken cancellationToken)
    {
        try
        {
            using (ScanProfiler.Measure("Update Episode Chapters"))
            {
                if (string.IsNullOrEmpty(existing.LocalPath))
                {
                    // No local path available for external chapter file lookup
                    return existing;
                }

                if (await _localChaptersProvider.UpdateChapters(
                        existing.Item,
                        Some(existing.LocalPath),
                        cancellationToken))
                {
                    existing.IsUpdated = true;
                }

                return existing;
            }
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }
}

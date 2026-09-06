using System.Collections.Immutable;
using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Scanner.Core.Errors;
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Interfaces.FFmpeg;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public class TelevisionFolderScanner : LocalFolderScanner, ITelevisionFolderScanner
{
    private readonly IFallbackMetadataProvider _fallbackMetadataProvider;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILocalChaptersProvider _localChaptersProvider;
    private readonly IScannerProxy _scannerProxy;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILocalMetadataProvider _localMetadataProvider;
    private readonly ILocalSubtitlesProvider _localSubtitlesProvider;
    private readonly ILogger<TelevisionFolderScanner> _logger;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly IMetadataRepository _metadataRepository;
    private readonly ITelevisionRepository _televisionRepository;

    public TelevisionFolderScanner(
        IScannerProxy scannerProxy,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ITelevisionRepository televisionRepository,
        ILocalStatisticsProvider localStatisticsProvider,
        ILocalMetadataProvider localMetadataProvider,
        ILocalSubtitlesProvider localSubtitlesProvider,
        ILocalChaptersProvider localChaptersProvider,
        IMetadataRepository metadataRepository,
        IImageCache imageCache,
        ILibraryRepository libraryRepository,
        IMediaItemRepository mediaItemRepository,
        IFFmpegPngService ffmpegPngService,
        ITempFilePool tempFilePool,
        IFallbackMetadataProvider fallbackMetadataProvider,
        ILogger<TelevisionFolderScanner> logger) : base(
        fileSystem,
        localStatisticsProvider,
        metadataRepository,
        mediaItemRepository,
        imageCache,
        ffmpegPngService,
        tempFilePool,
        logger)
    {
        _scannerProxy = scannerProxy;
        _fileSystem = fileSystem;
        _localFileSystem = localFileSystem;
        _televisionRepository = televisionRepository;
        _localMetadataProvider = localMetadataProvider;
        _localSubtitlesProvider = localSubtitlesProvider;
        _localChaptersProvider = localChaptersProvider;
        _metadataRepository = metadataRepository;
        _libraryRepository = libraryRepository;
        _mediaItemRepository = mediaItemRepository;
        _fallbackMetadataProvider = fallbackMetadataProvider;
        _logger = logger;
    }

    public async Task<Either<BaseError, Unit>> ScanFolder(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        decimal progressMin,
        decimal progressMax,
        CancellationToken cancellationToken)
    {
        try
        {
            decimal progressSpread = progressMax - progressMin;

            string normalizedLibraryPath = libraryPath.Path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (libraryPath.Path != normalizedLibraryPath)
            {
                await _libraryRepository.UpdatePath(libraryPath, normalizedLibraryPath);
            }

            ImmutableHashSet<string> allTrashedItems = await _mediaItemRepository.GetAllTrashedItems(libraryPath);

            var allShowFolders = _localFileSystem.ListSubdirectories(libraryPath.Path)
                .Filter(ShouldIncludeFolder)
                .OrderBy(identity)
                .ToList();

            foreach (string showFolder in allShowFolders)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                decimal percentCompletion = (decimal)allShowFolders.IndexOf(showFolder) / allShowFolders.Count;
                if (!await _scannerProxy.UpdateProgress(progressMin + percentCompletion * progressSpread, cancellationToken))
                {
                    return new ScanCanceled();
                }

                Option<int> maybeParentFolder =
                    await _libraryRepository.GetParentFolderId(libraryPath, showFolder, cancellationToken);

                // this folder is unused by the show, but will be used as parents of season folders
                LibraryFolder _ = await _libraryRepository.GetOrAddFolder(
                    libraryPath,
                    maybeParentFolder,
                    showFolder);

                Either<BaseError, MediaItemScanResult<Show>> maybeShow =
                    await FindOrCreateShow(libraryPath.Id, showFolder)
                        .BindT(show => UpdateMetadataForShow(show, showFolder))
                        .BindT(show => UpdateArtworkForShow(show, showFolder, ArtworkKind.Poster, cancellationToken))
                        .BindT(show => UpdateArtworkForShow(show, showFolder, ArtworkKind.FanArt, cancellationToken))
                        .BindT(show => UpdateArtworkForShow(
                            show,
                            showFolder,
                            ArtworkKind.Thumbnail,
                            cancellationToken));

                foreach (BaseError error in maybeShow.LeftToSeq())
                {
                    _logger.LogWarning(
                        "Error processing show in folder {Folder}: {Error}",
                        showFolder,
                        error.Value);
                }

                foreach (MediaItemScanResult<Show> result in maybeShow.RightToSeq())
                {
                    // add show to search index right away
                    if (result.IsAdded || result.IsUpdated)
                    {
                        if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                        {
                            _logger.LogWarning("Failed to reindex media items from scanner process");
                        }
                    }

                    Either<BaseError, Unit> scanResult = await ScanSeasons(
                        libraryPath,
                        ffmpegPath,
                        ffprobePath,
                        result.Item,
                        showFolder,
                        allTrashedItems,
                        cancellationToken);

                    foreach (ScanCanceled error in scanResult.LeftToSeq().OfType<ScanCanceled>())
                    {
                        return error;
                    }
                }
            }

            foreach (string path in await _televisionRepository.FindEpisodePaths(libraryPath))
            {
                if (!_fileSystem.File.Exists(path))
                {
                    _logger.LogInformation("Flagging missing episode at {Path}", path);

                    List<int> episodeIds = await FlagFileNotFound(libraryPath, path);
                    if (!await _scannerProxy.ReindexMediaItems(episodeIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to reindex media items from scanner process");
                    }
                }
                else if (Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Removing dot underscore file at {Path}", path);
                    await _televisionRepository.DeleteByPath(libraryPath, path);
                }
            }

            await _libraryRepository.CleanEtagsForLibraryPath(libraryPath);

            await _televisionRepository.DeleteEmptySeasons(libraryPath);
            List<int> ids = await _televisionRepository.DeleteEmptyShows(libraryPath);
            if (!await _scannerProxy.RemoveMediaItems(ids.ToArray(), cancellationToken))
            {
                _logger.LogWarning("Failed to remove media items from scanner process");
            }

            return Unit.Default;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return new ScanCanceled();
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Show>>> FindOrCreateShow(
        int libraryPathId,
        string showFolder)
    {
        ShowMetadata metadata = await _localMetadataProvider.GetMetadataForShow(showFolder);
        Option<Show> maybeShow = await _televisionRepository.GetShowByMetadata(libraryPathId, metadata, showFolder);

        foreach (Show show in maybeShow)
        {
            return new MediaItemScanResult<Show>(show);
        }

        return await _televisionRepository.AddShow(libraryPathId, metadata);
    }

    private async Task<Either<BaseError, Unit>> ScanSeasons(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        Show show,
        string showFolder,
        ImmutableHashSet<string> allTrashedItems,
        CancellationToken cancellationToken)
    {
        foreach (string seasonFolder in _localFileSystem.ListSubdirectories(showFolder).Filter(ShouldIncludeFolder)
                     .OrderBy(identity))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ScanCanceled();
            }

            Option<int> maybeParentFolder =
                await _libraryRepository.GetParentFolderId(libraryPath, seasonFolder, cancellationToken);

            string etag = FolderEtag.CalculateWithSubfolders(seasonFolder, _localFileSystem);
            LibraryFolder knownFolder = await _libraryRepository.GetOrAddFolder(
                libraryPath,
                maybeParentFolder,
                seasonFolder);

            // cache etag match for later checking
            // we still need to scan the season folder in case season artwork has changed
            bool etagMatch = knownFolder.Etag == etag;
            if (etagMatch)
            {
                if (allTrashedItems.Any(f => f.StartsWith(seasonFolder, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogDebug("Previously trashed items are now present in folder {Folder}", seasonFolder);
                    etagMatch = false;
                }
            }

            Option<int> maybeSeasonNumber = _fallbackMetadataProvider.GetSeasonNumberForFolder(seasonFolder);
            foreach (int seasonNumber in maybeSeasonNumber)
            {
                Either<BaseError, Season> maybeSeason = await _televisionRepository
                    .GetOrAddSeason(show, libraryPath.Id, seasonNumber)
                    .BindT(EnsureMetadataExists)
                    .BindT(season => UpdatePoster(season, seasonFolder, cancellationToken));

                foreach (BaseError error in maybeSeason.LeftToSeq())
                {
                    _logger.LogWarning(
                        "Error processing season in folder {Folder}: {Error}",
                        seasonFolder,
                        error.Value);
                }

                foreach (Season season in maybeSeason.RightToSeq())
                {
                    // skip scanning episodes when season folder etag matches
                    if (etagMatch)
                    {
                        continue;
                    }

                    Either<BaseError, Unit> scanResult = await ScanEpisodes(
                        libraryPath,
                        knownFolder,
                        ffmpegPath,
                        ffprobePath,
                        season,
                        seasonFolder,
                        cancellationToken);

                    foreach (ScanCanceled error in scanResult.LeftToSeq().OfType<ScanCanceled>())
                    {
                        return error;
                    }

                    // only set etag if no errors were encountered with the folder, and if reindex succeeds
                    if (scanResult.IsRight)
                    {
                        if (await _scannerProxy.ReindexMediaItems([season.Id], cancellationToken))
                        {
                            await _libraryRepository.SetEtag(libraryPath, knownFolder, seasonFolder, etag);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to reindex media items from scanner process");
                        }
                    }

                    season.Show = show;
                }
            }
        }

        return Unit.Default;
    }

    private async Task<Either<BaseError, Unit>> ScanEpisodes(
        LibraryPath libraryPath,
        LibraryFolder seasonFolder,
        string ffmpegPath,
        string ffprobePath,
        Season season,
        string seasonPath,
        CancellationToken cancellationToken)
    {
        var allSeasonFiles = _localFileSystem.ListSubdirectories(seasonPath)
            .Map(_localFileSystem.ListFiles)
            .Flatten()
            .Append(_localFileSystem.ListFiles(seasonPath))
            .Filter(f => VideoFileExtensions.Contains(Path.GetExtension(f)))
            .Filter(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
            .OrderBy(identity)
            .ToList();

        var hasErrors = false;

        foreach (string file in allSeasonFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ScanCanceled();
            }

            // TODO: figure out how to rebuild playlists
            Either<BaseError, Episode> maybeEpisode = await _televisionRepository
                .GetOrAddEpisode(season, libraryPath, seasonFolder, file, cancellationToken)
                .BindT(episode => UpdateStatistics(new MediaItemScanResult<Episode>(episode), ffmpegPath, ffprobePath)
                    .MapT(_ => episode))
                .BindT(video => UpdateLibraryFolderId(video, seasonFolder))
                .BindT(UpdateMetadata)
                .BindT(e => UpdateThumbnail(e, cancellationToken))
                .BindT(e => UpdateSubtitles(e, cancellationToken))
                .BindT(e => UpdateChapters(e, cancellationToken))
                .BindT(e => FlagNormal(new MediaItemScanResult<Episode>(e)))
                .MapT(r => r.Item);

            foreach (BaseError error in maybeEpisode.LeftToSeq())
            {
                _logger.LogWarning("Error processing episode at {Path}: {Error}", file, error.Value);
                hasErrors = true;
            }

            foreach (Episode episode in maybeEpisode.RightToSeq())
            {
                if (!await _scannerProxy.ReindexMediaItems([episode.Id], cancellationToken))
                {
                    _logger.LogWarning("Failed to reindex media items from scanner process");
                    hasErrors = true;
                }
            }
        }

        // TODO: remove missing episodes?

        if (hasErrors)
        {
            return new ErrorProcessingFolder();
        }

        return Unit.Default;
    }

    private async Task<Either<BaseError, MediaItemScanResult<Show>>> UpdateMetadataForShow(
        MediaItemScanResult<Show> result,
        string showFolder)
    {
        try
        {
            Show show = result.Item;

            Option<string> maybeNfo = LocateNfoFileForShow(showFolder);
            if (maybeNfo.IsNone)
            {
                if (!Optional(show.ShowMetadata).Flatten().Any())
                {
                    _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", showFolder);
                    if (await _localMetadataProvider.RefreshFallbackMetadata(show, showFolder))
                    {
                        result.IsUpdated = true;
                    }
                }
            }

            foreach (string nfoFile in maybeNfo)
            {
                bool shouldUpdate = Optional(show.ShowMetadata).Flatten().HeadOrNone().Match(
                    m => m.MetadataKind == MetadataKind.Fallback ||
                         m.DateUpdated != _localFileSystem.GetLastWriteTime(nfoFile),
                    true);

                if (shouldUpdate)
                {
                    _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                    if (await _localMetadataProvider.RefreshSidecarMetadata(show, nfoFile))
                    {
                        result.IsUpdated = true;
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Season>> EnsureMetadataExists(Season season)
    {
        season.SeasonMetadata ??= [];

        if (season.SeasonMetadata.Count == 0)
        {
            var metadata = new SeasonMetadata
            {
                SeasonId = season.Id,
                Season = season,
                DateAdded = DateTime.UtcNow,
                Guids = [],
                Tags = [],
                Artwork = []
            };

            season.SeasonMetadata.Add(metadata);
            await _metadataRepository.Add(metadata);
        }

        return season;
    }

    private async Task<Either<BaseError, Episode>> UpdateLibraryFolderId(Episode episode, LibraryFolder libraryFolder)
    {
        MediaFile mediaFile = episode.GetHeadVersion().MediaFiles.Head();
        if (mediaFile.LibraryFolderId != libraryFolder.Id)
        {
            await _libraryRepository.UpdateLibraryFolderId(mediaFile, libraryFolder.Id);
        }

        return episode;
    }

    private async Task<Either<BaseError, Episode>> UpdateMetadata(Episode episode)
    {
        try
        {
            Option<string> maybeNfo = LocateNfoFile(episode);
            if (maybeNfo.IsNone)
            {
                bool shouldUpdate = Optional(episode.EpisodeMetadata).Flatten().HeadOrNone().Match(
                    m => m.DateUpdated == SystemTime.MinValueUtc,
                    true);

                if (shouldUpdate)
                {
                    string path = episode.MediaVersions.Head().MediaFiles.Head().Path;
                    _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", path);
                    await _localMetadataProvider.RefreshFallbackMetadata(episode);
                }
            }

            foreach (string nfoFile in maybeNfo)
            {
                bool shouldUpdate = Optional(episode.EpisodeMetadata).Flatten().HeadOrNone().Match(
                    m => m.MetadataKind == MetadataKind.Fallback ||
                         m.DateUpdated != _localFileSystem.GetLastWriteTime(nfoFile),
                    true);

                if (shouldUpdate)
                {
                    _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                    await _localMetadataProvider.RefreshSidecarMetadata(episode, nfoFile);
                }
            }

            return episode;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Show>>> UpdateArtworkForShow(
        MediaItemScanResult<Show> result,
        string showFolder,
        ArtworkKind artworkKind,
        CancellationToken cancellationToken)
    {
        try
        {
            Show show = result.Item;
            foreach (ShowMetadata metadata in show.ShowMetadata.HeadOrNone())
            {
                Option<string> maybeArtwork = LocateArtworkForShow(showFolder, artworkKind);
                foreach (string artworkFile in maybeArtwork)
                {
                    await RefreshArtwork(artworkFile, metadata, artworkKind, None, None, cancellationToken);
                }

                if (maybeArtwork.IsNone && metadata.Artwork.Any(a => a.ArtworkKind == artworkKind))
                {
                    await _metadataRepository.RemoveArtworkWithKind(metadata, artworkKind);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Season>> UpdatePoster(
        Season season,
        string seasonFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (SeasonMetadata metadata in season.SeasonMetadata.HeadOrNone())
            {
                Option<string> maybePoster = LocatePoster(season, seasonFolder);
                foreach (string posterFile in maybePoster)
                {
                    await RefreshArtwork(posterFile, metadata, ArtworkKind.Poster, None, None, cancellationToken);
                }

                if (maybePoster.IsNone && metadata.Artwork.Any(a => a.ArtworkKind is ArtworkKind.Poster))
                {
                    await _metadataRepository.RemoveArtworkWithKind(metadata, ArtworkKind.Poster);
                }
            }

            return season;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Episode>> UpdateThumbnail(Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            foreach (EpisodeMetadata metadata in episode.EpisodeMetadata)
            {
                Option<string> maybeThumbnail = LocateThumbnail(episode);
                foreach (string thumbnailFile in maybeThumbnail)
                {
                    await RefreshArtwork(
                        thumbnailFile,
                        metadata,
                        ArtworkKind.Thumbnail,
                        None,
                        None,
                        cancellationToken);
                }

                if (maybeThumbnail.IsNone && metadata.Artwork.Any(a => a.ArtworkKind is ArtworkKind.Thumbnail))
                {
                    await _metadataRepository.RemoveArtworkWithKind(metadata, ArtworkKind.Thumbnail);
                }
            }

            return episode;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Episode>> UpdateSubtitles(Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            await _localSubtitlesProvider.UpdateSubtitles(episode, None, true, cancellationToken);
            return episode;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Episode>> UpdateChapters(Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            await _localChaptersProvider.UpdateChapters(episode, None, cancellationToken);
            return episode;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private Option<string> LocateNfoFileForShow(string showFolder) =>
        Optional(Path.Combine(showFolder, "tvshow.nfo")).Filter(s => _fileSystem.File.Exists(s));

    private Option<string> LocateNfoFile(Episode episode)
    {
        string path = episode.MediaVersions.Head().MediaFiles.Head().Path;
        return Optional(Path.ChangeExtension(path, "nfo")).Filter(s => _fileSystem.File.Exists(s));
    }

    private Option<string> LocateArtworkForShow(string showFolder, ArtworkKind artworkKind)
    {
        string[] segments = artworkKind switch
        {
            ArtworkKind.Poster => ["poster", "folder"],
            ArtworkKind.FanArt => ["fanart"],
            ArtworkKind.Thumbnail => ["thumb"],
            _ => throw new ArgumentOutOfRangeException(nameof(artworkKind))
        };

        return ImageFileExtensions
            .Map(ext => segments.Map(segment => $"{segment}.{ext}"))
            .Flatten()
            .Map(f => Path.Combine(showFolder, f))
            .Filter(s => _fileSystem.File.Exists(s))
            .HeadOrNone();
    }

    private Option<string> LocatePoster(Season season, string seasonFolder)
    {
        string folder = Path.GetDirectoryName(seasonFolder) ?? string.Empty;
        return ImageFileExtensions
            .Map(ext => Path.Combine(folder, $"season{season.SeasonNumber:00}-poster.{ext}"))
            .Filter(s => _fileSystem.File.Exists(s))
            .HeadOrNone();
    }

    private Option<string> LocateThumbnail(Episode episode)
    {
        string path = episode.MediaVersions.Head().MediaFiles.Head().Path;
        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        return ImageFileExtensions
            .Map(ext => Path.GetFileNameWithoutExtension(path) + $"-thumb.{ext}")
            .Map(f => Path.Combine(folder, f))
            .Filter(f => _fileSystem.File.Exists(f))
            .HeadOrNone();
    }
}

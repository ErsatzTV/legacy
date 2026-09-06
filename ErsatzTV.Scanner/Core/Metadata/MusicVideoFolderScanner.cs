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
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Interfaces.FFmpeg;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public class MusicVideoFolderScanner : LocalFolderScanner, IMusicVideoFolderScanner
{
    private readonly IArtistRepository _artistRepository;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILocalChaptersProvider _localChaptersProvider;
    private readonly IMetadataRepository _metadataRepository;
    private readonly IScannerProxy _scannerProxy;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILocalMetadataProvider _localMetadataProvider;
    private readonly ILocalSubtitlesProvider _localSubtitlesProvider;
    private readonly ILogger<MusicVideoFolderScanner> _logger;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly IMusicVideoRepository _musicVideoRepository;

    public MusicVideoFolderScanner(
        IScannerProxy scannerProxy,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ILocalStatisticsProvider localStatisticsProvider,
        ILocalMetadataProvider localMetadataProvider,
        ILocalSubtitlesProvider localSubtitlesProvider,
        ILocalChaptersProvider localChaptersProvider,
        IMetadataRepository metadataRepository,
        IImageCache imageCache,
        IArtistRepository artistRepository,
        IMusicVideoRepository musicVideoRepository,
        ILibraryRepository libraryRepository,
        IMediaItemRepository mediaItemRepository,
        IFFmpegPngService ffmpegPngService,
        ITempFilePool tempFilePool,
        ILogger<MusicVideoFolderScanner> logger) : base(
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
        _localMetadataProvider = localMetadataProvider;
        _localSubtitlesProvider = localSubtitlesProvider;
        _localChaptersProvider = localChaptersProvider;
        _metadataRepository = metadataRepository;
        _artistRepository = artistRepository;
        _musicVideoRepository = musicVideoRepository;
        _libraryRepository = libraryRepository;
        _mediaItemRepository = mediaItemRepository;
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

            var allArtistFolders = _localFileSystem.ListSubdirectories(libraryPath.Path)
                .Filter(ShouldIncludeFolder)
                .OrderBy(identity)
                .ToList();

            foreach (string artistFolder in allArtistFolders)
            {
                // _logger.LogDebug("Scanning artist folder {Folder}", artistFolder);
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                decimal percentCompletion = (decimal)allArtistFolders.IndexOf(artistFolder) / allArtistFolders.Count;
                if (!await _scannerProxy.UpdateProgress(
                        progressMin + percentCompletion * progressSpread,
                        cancellationToken))
                {
                    return new ScanCanceled();
                }

                Either<BaseError, MediaItemScanResult<Artist>> maybeArtist =
                    await FindOrCreateArtist(libraryPath.Id, artistFolder)
                        .BindT(artist => UpdateMetadataForArtist(artist, artistFolder))
                        .BindT(artist => UpdateArtworkForArtist(
                            artist,
                            artistFolder,
                            ArtworkKind.Thumbnail,
                            cancellationToken))
                        .BindT(artist => UpdateArtworkForArtist(
                            artist,
                            artistFolder,
                            ArtworkKind.FanArt,
                            cancellationToken));

                foreach (BaseError error in maybeArtist.LeftToSeq())
                {
                    _logger.LogWarning(
                        "Error processing artist in folder {Folder}: {Error}",
                        artistFolder,
                        error.Value);
                }

                foreach (MediaItemScanResult<Artist> result in maybeArtist.RightToSeq())
                {
                    if (result.IsAdded || result.IsUpdated)
                    {
                        if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                        {
                            _logger.LogWarning("Failed to reindex media items from scanner process");
                        }
                    }

                    Either<BaseError, Unit> scanResult = await ScanMusicVideos(
                        libraryPath,
                        ffmpegPath,
                        ffprobePath,
                        result.Item,
                        artistFolder,
                        allTrashedItems,
                        cancellationToken);

                    foreach (ScanCanceled error in scanResult.LeftToSeq().OfType<ScanCanceled>())
                    {
                        return error;
                    }
                }
            }

            foreach (string path in await _musicVideoRepository.FindOrphanPaths(libraryPath))
            {
                _logger.LogInformation("Removing improperly named music video at {Path}", path);
                List<int> musicVideoIds = await _musicVideoRepository.DeleteByPath(libraryPath, path);
                if (!await _scannerProxy.RemoveMediaItems(musicVideoIds.ToArray(), cancellationToken))
                {
                    _logger.LogWarning("Failed to remove media items from scanner process");
                }
            }

            foreach (string path in await _musicVideoRepository.FindMusicVideoPaths(libraryPath))
            {
                if (!_fileSystem.File.Exists(path))
                {
                    _logger.LogInformation("Flagging missing music video at {Path}", path);
                    List<int> musicVideoIds = await FlagFileNotFound(libraryPath, path);
                    if (!await _scannerProxy.ReindexMediaItems(musicVideoIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to reindex media items from scanner process");
                    }
                }
                else if (Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Removing dot underscore file at {Path}", path);
                    List<int> musicVideoIds = await _musicVideoRepository.DeleteByPath(libraryPath, path);
                    if (!await _scannerProxy.RemoveMediaItems(musicVideoIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to remove media items from scanner process");
                    }
                }
            }

            await _libraryRepository.CleanEtagsForLibraryPath(libraryPath);

            List<int> artistIds = await _artistRepository.DeleteEmptyArtists(libraryPath);
            if (!await _scannerProxy.RemoveMediaItems(artistIds.ToArray(), cancellationToken))
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

    private async Task<Either<BaseError, MediaItemScanResult<Artist>>> FindOrCreateArtist(
        int libraryPathId,
        string artistFolder)
    {
        ArtistMetadata metadata = await _localMetadataProvider.GetMetadataForArtist(artistFolder);
        Option<Artist> maybeArtist = await _artistRepository.GetArtistByMetadata(libraryPathId, metadata);
        return await maybeArtist.Match(
            artist => Right<BaseError, MediaItemScanResult<Artist>>(new MediaItemScanResult<Artist>(artist))
                .AsTask(),
            async () => await _artistRepository.AddArtist(libraryPathId, artistFolder, metadata));
    }

    private async Task<Either<BaseError, MediaItemScanResult<Artist>>> UpdateMetadataForArtist(
        MediaItemScanResult<Artist> result,
        string artistFolder)
    {
        try
        {
            Artist artist = result.Item;
            await LocateNfoFileForArtist(artistFolder).Match(
                async nfoFile =>
                {
                    bool shouldUpdate = Optional(artist.ArtistMetadata).Flatten().HeadOrNone().Match(
                        m => m.MetadataKind == MetadataKind.Fallback ||
                             m.DateUpdated != _localFileSystem.GetLastWriteTime(nfoFile),
                        true);

                    if (shouldUpdate)
                    {
                        _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                        if (await _localMetadataProvider.RefreshSidecarMetadata(artist, nfoFile))
                        {
                            result.IsUpdated = true;
                        }
                    }
                },
                async () =>
                {
                    if (!Optional(artist.ArtistMetadata).Flatten().Any())
                    {
                        _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", artistFolder);
                        if (await _localMetadataProvider.RefreshFallbackMetadata(artist, artistFolder))
                        {
                            result.IsUpdated = true;
                        }
                    }
                });

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Artist>>> UpdateArtworkForArtist(
        MediaItemScanResult<Artist> result,
        string artistFolder,
        ArtworkKind artworkKind,
        CancellationToken cancellationToken)
    {
        try
        {
            Artist artist = result.Item;
            foreach (ArtistMetadata metadata in artist.ArtistMetadata.HeadOrNone())
            {
                Option<string> maybeArtwork = LocateArtworkForArtist(artistFolder, artworkKind);
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

    private async Task<Either<BaseError, Unit>> ScanMusicVideos(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        Artist artist,
        string artistFolder,
        ImmutableHashSet<string> allTrashedItems,
        CancellationToken cancellationToken)
    {
        var folderQueue = new Queue<string>();
        folderQueue.Enqueue(artistFolder);

        while (folderQueue.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ScanCanceled();
            }

            string musicVideoFolder = folderQueue.Dequeue();
            Option<int> maybeParentFolder = await _libraryRepository.GetParentFolderId(
                libraryPath,
                musicVideoFolder,
                cancellationToken);

            // _logger.LogDebug("Scanning music video folder {Folder}", musicVideoFolder);

            var allFiles = _localFileSystem.ListFiles(musicVideoFolder)
                .Filter(f => VideoFileExtensions.Contains(Path.GetExtension(f)))
                .Filter(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (string subdirectory in _localFileSystem.ListSubdirectories(musicVideoFolder).OrderBy(identity))
            {
                folderQueue.Enqueue(subdirectory);
            }

            string etag = FolderEtag.Calculate(musicVideoFolder, _localFileSystem);
            LibraryFolder knownFolder = await _libraryRepository.GetOrAddFolder(
                libraryPath,
                maybeParentFolder,
                musicVideoFolder);

            if (knownFolder.Etag == etag)
            {
                if (allFiles.Any(allTrashedItems.Contains))
                {
                    _logger.LogDebug("Previously trashed items are now present in folder {Folder}", musicVideoFolder);
                }
                else
                {
                    // etag matches and no trashed items are now present, continue to next folder
                    continue;
                }
            }
            else
            {
                _logger.LogDebug(
                    "UPDATE: Etag has changed for folder {Folder}",
                    musicVideoFolder);
            }

            var hasErrors = false;

            foreach (string file in allFiles.OrderBy(identity))
            {
                // TODO: figure out how to rebuild playouts
                Either<BaseError, MediaItemScanResult<MusicVideo>> maybeMusicVideo = await _musicVideoRepository
                    .GetOrAdd(artist, libraryPath, knownFolder, file)
                    .BindT(musicVideo => UpdateStatistics(musicVideo, ffmpegPath, ffprobePath))
                    .BindT(video => UpdateLibraryFolderId(video, knownFolder))
                    .BindT(UpdateMetadata)
                    .BindT(result => UpdateThumbnail(result, cancellationToken))
                    .BindT(result => UpdateSubtitles(result, cancellationToken))
                    .BindT(result => UpdateChapters(result, cancellationToken))
                    .BindT(FlagNormal);

                foreach (BaseError error in maybeMusicVideo.LeftToSeq())
                {
                    _logger.LogWarning("Error processing music video at {Path}: {Error}", file, error.Value);
                    hasErrors = true;
                }

                foreach (MediaItemScanResult<MusicVideo> result in maybeMusicVideo.RightToSeq())
                {
                    if (result.IsAdded || result.IsUpdated)
                    {
                        if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                        {
                            _logger.LogWarning("Failed to reindex media items from scanner process");
                            hasErrors = true;
                        }
                    }
                }
            }

            // only do this once per folder and only if all files processed successfully
            if (!hasErrors)
            {
                await _libraryRepository.SetEtag(libraryPath, knownFolder, musicVideoFolder, etag);
            }
        }

        return Unit.Default;
    }

    private async Task<Either<BaseError, MediaItemScanResult<MusicVideo>>> UpdateLibraryFolderId(
        MediaItemScanResult<MusicVideo> video,
        LibraryFolder libraryFolder)
    {
        MediaFile mediaFile = video.Item.GetHeadVersion().MediaFiles.Head();
        if (mediaFile.LibraryFolderId != libraryFolder.Id)
        {
            await _libraryRepository.UpdateLibraryFolderId(mediaFile, libraryFolder.Id);
            video.IsUpdated = true;
        }

        return video;
    }

    private async Task<Either<BaseError, MediaItemScanResult<MusicVideo>>> UpdateMetadata(
        MediaItemScanResult<MusicVideo> result)
    {
        try
        {
            MusicVideo musicVideo = result.Item;

            Option<string> maybeNfoFile = LocateNfoFile(musicVideo);
            if (maybeNfoFile.IsNone)
            {
                if (!Optional(musicVideo.MusicVideoMetadata).Flatten().Any())
                {
                    musicVideo.MusicVideoMetadata ??= [];

                    string path = musicVideo.MediaVersions.Head().MediaFiles.Head().Path;
                    _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", path);
                    if (await _localMetadataProvider.RefreshFallbackMetadata(musicVideo))
                    {
                        result.IsUpdated = true;
                    }
                }
            }

            foreach (string nfoFile in maybeNfoFile)
            {
                bool shouldUpdate = Optional(musicVideo.MusicVideoMetadata).Flatten().HeadOrNone().Match(
                    m => m.MetadataKind == MetadataKind.Fallback ||
                         m.DateUpdated != _localFileSystem.GetLastWriteTime(nfoFile),
                    true);

                if (shouldUpdate)
                {
                    _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                    if (await _localMetadataProvider.RefreshSidecarMetadata(musicVideo, nfoFile))
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

    private Option<string> LocateNfoFileForArtist(string artistFolder) =>
        Optional(Path.Combine(artistFolder, "artist.nfo")).Filter(s => _fileSystem.File.Exists(s));

    private Option<string> LocateArtworkForArtist(string artistFolder, ArtworkKind artworkKind)
    {
        string segment = artworkKind switch
        {
            ArtworkKind.Thumbnail => "thumb",
            ArtworkKind.FanArt => "fanart",
            _ => throw new ArgumentOutOfRangeException(nameof(artworkKind))
        };

        return ImageFileExtensions
            .Map(ext => $"{segment}.{ext}")
            .Map(f => Path.Combine(artistFolder, f))
            .Filter(s => _fileSystem.File.Exists(s))
            .HeadOrNone();
    }

    private Option<string> LocateNfoFile(MusicVideo musicVideo)
    {
        string path = musicVideo.MediaVersions.Head().MediaFiles.Head().Path;
        return Optional(Path.ChangeExtension(path, "nfo"))
            .Filter(s => _fileSystem.File.Exists(s))
            .HeadOrNone();
    }

    private async Task<Either<BaseError, MediaItemScanResult<MusicVideo>>> UpdateThumbnail(
        MediaItemScanResult<MusicVideo> result,
        CancellationToken cancellationToken)
    {
        try
        {
            MusicVideo musicVideo = result.Item;

            foreach (MusicVideoMetadata metadata in musicVideo.MusicVideoMetadata.HeadOrNone())
            {
                Option<string> maybeThumbnail = LocateThumbnail(musicVideo);
                foreach (string thumbnailFile in maybeThumbnail)
                {
                    await RefreshArtwork(thumbnailFile, metadata, ArtworkKind.Thumbnail, None, None, cancellationToken);
                }

                if (maybeThumbnail.IsNone && metadata.Artwork.Any(a => a.ArtworkKind is ArtworkKind.Thumbnail))
                {
                    await _metadataRepository.RemoveArtworkWithKind(metadata, ArtworkKind.Thumbnail);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<MusicVideo>>> UpdateSubtitles(
        MediaItemScanResult<MusicVideo> result,
        CancellationToken cancellationToken)
    {
        try
        {
            await _localSubtitlesProvider.UpdateSubtitles(result.Item, None, true, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<MusicVideo>>> UpdateChapters(
        MediaItemScanResult<MusicVideo> result,
        CancellationToken cancellationToken)
    {
        try
        {
            await _localChaptersProvider.UpdateChapters(result.Item, None, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private Option<string> LocateThumbnail(MusicVideo musicVideo)
    {
        string path = musicVideo.MediaVersions.Head().MediaFiles.Head().Path;

        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string filenameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        string thumbFilename = filenameWithoutExtension + "-thumb" + extension;
        string thumbPath = Path.Combine(directory, thumbFilename);

        return ImageFileExtensions
            .SelectMany(ext => new[] { Path.ChangeExtension(path, ext), Path.ChangeExtension(thumbPath, ext) })
            .Filter(f => _fileSystem.File.Exists(f))
            .HeadOrNone();
    }
}

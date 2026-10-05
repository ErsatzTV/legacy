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

public class SongFolderScanner : LocalFolderScanner, ISongFolderScanner
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IScannerProxy _scannerProxy;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILocalMetadataProvider _localMetadataProvider;
    private readonly IMetadataRepository _metadataRepository;
    private readonly ILogger<SongFolderScanner> _logger;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly ISongRepository _songRepository;

    public SongFolderScanner(
        IScannerProxy scannerProxy,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ILocalStatisticsProvider localStatisticsProvider,
        ILocalMetadataProvider localMetadataProvider,
        IMetadataRepository metadataRepository,
        IImageCache imageCache,
        ISongRepository songRepository,
        ILibraryRepository libraryRepository,
        IMediaItemRepository mediaItemRepository,
        IFFmpegPngService ffmpegPngService,
        ITempFilePool tempFilePool,
        ILogger<SongFolderScanner> logger) : base(
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
        _metadataRepository = metadataRepository;
        _songRepository = songRepository;
        _libraryRepository = libraryRepository;
        _mediaItemRepository = mediaItemRepository;
        _logger = logger;
    }

    public async Task<Either<BaseError, Unit>> ScanFolder(
        LibraryPath libraryPath,
        string ffprobePath,
        string ffmpegPath,
        decimal progressMin,
        decimal progressMax,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        try
        {
            decimal progressSpread = progressMax - progressMin;

            var foldersCompleted = 0;

            var folderQueue = new Queue<string>();

            string normalizedLibraryPath = libraryPath.Path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (libraryPath.Path != normalizedLibraryPath)
            {
                await _libraryRepository.UpdatePath(libraryPath, normalizedLibraryPath);
            }

            ImmutableHashSet<string> allTrashedItems = await _mediaItemRepository.GetAllTrashedItems(libraryPath);

            if (ShouldIncludeFolder(libraryPath.Path))
            {
                folderQueue.Enqueue(libraryPath.Path);
            }

            foreach (string folder in _localFileSystem.ListSubdirectories(libraryPath.Path)
                         .Filter(ShouldIncludeFolder)
                         .OrderBy(identity))
            {
                folderQueue.Enqueue(folder);
            }

            while (folderQueue.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                decimal percentCompletion = (decimal)foldersCompleted / (foldersCompleted + folderQueue.Count);
                if (!await _scannerProxy.UpdateProgress(progressMin + percentCompletion * progressSpread, cancellationToken))
                {
                    return new ScanCanceled();
                }

                string songFolder = folderQueue.Dequeue();
                Option<int> maybeParentFolder =
                    await _libraryRepository.GetParentFolderId(libraryPath, songFolder, cancellationToken);

                foldersCompleted++;

                var filesForEtag = _localFileSystem.ListFiles(songFolder).ToList();

                var allFiles = filesForEtag
                    .Filter(f => AudioFileExtensions.Contains(Path.GetExtension(f)))
                    .Filter(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (string subdirectory in _localFileSystem.ListSubdirectories(songFolder)
                             .Filter(ShouldIncludeFolder)
                             .OrderBy(identity))
                {
                    folderQueue.Enqueue(subdirectory);
                }

                string etag = FolderEtag.Calculate(songFolder, _localFileSystem);
                LibraryFolder knownFolder = await _libraryRepository.GetOrAddFolder(
                    libraryPath,
                    maybeParentFolder,
                    songFolder);

                if (knownFolder.Etag == etag)
                {
                    if (allFiles.Any(allTrashedItems.Contains))
                    {
                        _logger.LogDebug("Previously trashed items are now present in folder {Folder}", songFolder);
                    }
                    else if (!deepScan)
                    {
                        // etag matches and no trashed items are now present, continue to next folder
                        continue;
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "UPDATE: Etag has changed for folder {Folder}",
                        songFolder);
                }

                _logger.LogDebug(
                    "UPDATE: Etag has changed for folder {Folder}",
                    songFolder);

                var hasErrors = false;

                foreach (string file in allFiles.OrderBy(identity))
                {
                    Either<BaseError, MediaItemScanResult<Song>> maybeSong = await _songRepository
                        .GetOrAdd(libraryPath, knownFolder, file)
                        .BindT(video => UpdateStatistics(video, ffmpegPath, ffprobePath, deepScan))
                        .BindT(video => UpdateLibraryFolderId(video, knownFolder))
                        .BindT(video => UpdateMetadata(video, deepScan))
                        .BindT(video => UpdateThumbnail(video, knownFolder, ffmpegPath, deepScan, cancellationToken))
                        .BindT(FlagNormal);

                    foreach (BaseError error in maybeSong.LeftToSeq())
                    {
                        _logger.LogWarning("Error processing song at {Path}: {Error}", file, error.Value);
                        hasErrors = true;
                    }

                    foreach (MediaItemScanResult<Song> result in maybeSong.RightToSeq())
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
                    await _libraryRepository.SetEtag(libraryPath, knownFolder, songFolder, etag);
                }
            }

            foreach (string path in await _songRepository.FindSongPaths(libraryPath))
            {
                if (!_fileSystem.File.Exists(path))
                {
                    _logger.LogInformation("Flagging missing song at {Path}", path);
                    List<int> songIds = await FlagFileNotFound(libraryPath, path);
                    if (!await _scannerProxy.ReindexMediaItems(songIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to reindex media items from scanner process");
                    }
                }
                else if (Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Removing dot underscore file at {Path}", path);
                    List<int> songIds = await _songRepository.DeleteByPath(libraryPath, path);
                    if (!await _scannerProxy.RemoveMediaItems(songIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to remove media items from scanner process");
                    }
                }
            }

            await _libraryRepository.CleanEtagsForLibraryPath(libraryPath);

            return Unit.Default;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return new ScanCanceled();
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Song>>> UpdateLibraryFolderId(
        MediaItemScanResult<Song> result,
        LibraryFolder libraryFolder)
    {
        MediaFile mediaFile = result.Item.GetHeadVersion().MediaFiles.Head();
        if (mediaFile.LibraryFolderId != libraryFolder.Id)
        {
            await _libraryRepository.UpdateLibraryFolderId(mediaFile, libraryFolder.Id);
            result.IsUpdated = true;
        }

        return result;
    }

    private async Task<Either<BaseError, MediaItemScanResult<Song>>> UpdateMetadata(
        MediaItemScanResult<Song> result,
        bool deepScan)
    {
        try
        {
            Song song = result.Item;
            string path = song.GetHeadVersion().MediaFiles.Head().Path;

            bool shouldUpdate = deepScan || Optional(song.SongMetadata).Flatten().HeadOrNone().Match(
                m => m.MetadataKind == MetadataKind.Fallback ||
                     m.DateUpdated != _localFileSystem.GetLastWriteTime(path),
                true);

            if (shouldUpdate)
            {
                song.SongMetadata ??= [];

                _logger.LogDebug("Refreshing {Attribute} for {Path}", "Metadata", path);
                if (await _localMetadataProvider.RefreshTagMetadata(song))
                {
                    result.IsUpdated = true;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Song>>> UpdateThumbnail(
        MediaItemScanResult<Song> result,
        LibraryFolder knownFolder,
        string ffmpegPath,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        try
        {
            // reload the song from the database at this point
            if (result.IsAdded)
            {
                LibraryPath libraryPath = result.Item.LibraryPath;
                string path = result.Item.GetHeadVersion().MediaFiles.Head().Path;
                foreach (MediaItemScanResult<Song> s in (await _songRepository.GetOrAdd(libraryPath, knownFolder, path))
                         .RightToSeq())
                {
                    result.Item = s.Item;
                }
            }

            Song song = result.Item;

            foreach (SongMetadata metadata in song.SongMetadata.HeadOrNone())
            {
                Option<string> maybeThumbnail = LocateThumbnail(song);
                if (maybeThumbnail.IsNone &&
                    !await ExtractEmbeddedArtwork(song, ffmpegPath, deepScan, cancellationToken))
                {
                    if (metadata.Artwork.Any(a => a.ArtworkKind is ArtworkKind.Thumbnail))
                    {
                        await _metadataRepository.RemoveArtworkWithKind(metadata, ArtworkKind.Thumbnail);
                    }
                }

                foreach (string thumbnailFile in maybeThumbnail)
                {
                    await RefreshArtwork(
                        thumbnailFile,
                        metadata,
                        ArtworkKind.Thumbnail,
                        ffmpegPath,
                        None,
                        deepScan,
                        cancellationToken);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private Option<string> LocateThumbnail(Song song)
    {
        string path = song.MediaVersions.Head().MediaFiles.Head().Path;
        Option<DirectoryInfo> parent = Optional(Directory.GetParent(path));

        return parent.Map(di =>
        {
            string coverPath = Path.Combine(di.FullName, "cover.jpg");
            return ImageFileExtensions
                .Map(ext => Path.ChangeExtension(coverPath, ext))
                .Filter(f => _fileSystem.File.Exists(f))
                .HeadOrNone();
        }).Flatten();
    }

    private async Task<bool> ExtractEmbeddedArtwork(
        Song song,
        string ffmpegPath,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        Option<MediaStream> maybeArtworkStream = Optional(song.GetHeadVersion().Streams.Find(ms => ms.AttachedPic));
        foreach (MediaStream artworkStream in maybeArtworkStream)
        {
            return await RefreshArtwork(
                song.GetHeadVersion().MediaFiles.Head().Path,
                song.SongMetadata.Head(),
                ArtworkKind.Thumbnail,
                ffmpegPath,
                artworkStream.Index,
                deepScan,
                cancellationToken);
        }

        return false;
    }
}

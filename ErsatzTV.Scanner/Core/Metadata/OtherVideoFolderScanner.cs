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

public class OtherVideoFolderScanner : LocalFolderScanner, IOtherVideoFolderScanner
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILocalChaptersProvider _localChaptersProvider;
    private readonly IMetadataRepository _metadataRepository;
    private readonly IScannerProxy _scannerProxy;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILocalMetadataProvider _localMetadataProvider;
    private readonly ILocalSubtitlesProvider _localSubtitlesProvider;
    private readonly ILogger<OtherVideoFolderScanner> _logger;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly IOtherVideoRepository _otherVideoRepository;

    public OtherVideoFolderScanner(
        IScannerProxy scannerProxy,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ILocalStatisticsProvider localStatisticsProvider,
        ILocalMetadataProvider localMetadataProvider,
        ILocalSubtitlesProvider localSubtitlesProvider,
        ILocalChaptersProvider localChaptersProvider,
        IMetadataRepository metadataRepository,
        IImageCache imageCache,
        IOtherVideoRepository otherVideoRepository,
        ILibraryRepository libraryRepository,
        IMediaItemRepository mediaItemRepository,
        IFFmpegPngService ffmpegPngService,
        ITempFilePool tempFilePool,
        ILogger<OtherVideoFolderScanner> logger) : base(
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
        _otherVideoRepository = otherVideoRepository;
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

            var foldersCompleted = 0;

            var allFolders = new System.Collections.Generic.HashSet<string>();
            var folderQueue = new Queue<string>();

            ImmutableHashSet<string> allTrashedItems = await _mediaItemRepository.GetAllTrashedItems(libraryPath);

            string normalizedLibraryPath = libraryPath.Path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (libraryPath.Path != normalizedLibraryPath)
            {
                _logger.LogDebug(
                    "Normalizing library path from {Original} to {Normalized}",
                    libraryPath.Path,
                    normalizedLibraryPath);
                await _libraryRepository.UpdatePath(libraryPath, normalizedLibraryPath);
            }

            if (ShouldIncludeFolder(libraryPath.Path) && allFolders.Add(libraryPath.Path))
            {
                _logger.LogDebug("Adding folder to scanner queue: {Folder}", libraryPath.Path);
                folderQueue.Enqueue(libraryPath.Path);
            }

            foreach (string folder in _localFileSystem.ListSubdirectories(libraryPath.Path)
                         .Filter(ShouldIncludeFolder)
                         .Filter(allFolders.Add)
                         .OrderBy(identity))
            {
                _logger.LogDebug("Adding folder to scanner queue: {Folder}", folder);
                folderQueue.Enqueue(folder);
            }

            while (folderQueue.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                decimal percentCompletion = (decimal)foldersCompleted / (foldersCompleted + folderQueue.Count);
                if (!await _scannerProxy.UpdateProgress(
                        progressMin + percentCompletion * progressSpread,
                        cancellationToken))
                {
                    return new ScanCanceled();
                }

                string otherVideoFolder = folderQueue.Dequeue();
                Option<int> maybeParentFolder =
                    await _libraryRepository.GetParentFolderId(libraryPath, otherVideoFolder, cancellationToken);

                foldersCompleted++;

                var filesForEtag = _localFileSystem.ListFiles(otherVideoFolder).ToList();

                var allFiles = filesForEtag
                    .Filter(f => VideoFileExtensions.Contains(Path.GetExtension(f)))
                    .Filter(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (string subdirectory in _localFileSystem.ListSubdirectories(otherVideoFolder)
                             .Filter(ShouldIncludeFolder)
                             .Filter(allFolders.Add)
                             .OrderBy(identity))
                {
                    _logger.LogDebug("Adding folder to scanner queue: {Folder}", subdirectory);
                    folderQueue.Enqueue(subdirectory);
                }

                string etag = FolderEtag.Calculate(otherVideoFolder, _localFileSystem);
                LibraryFolder knownFolder = await _libraryRepository.GetOrAddFolder(
                    libraryPath,
                    maybeParentFolder,
                    otherVideoFolder);

                if (knownFolder.Etag == etag)
                {
                    if (allFiles.Any(allTrashedItems.Contains))
                    {
                        _logger.LogDebug(
                            "Previously trashed items are now present in folder {Folder}",
                            otherVideoFolder);
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
                        otherVideoFolder);
                }

                var hasErrors = false;

                foreach (string file in allFiles.OrderBy(identity))
                {
                    _logger.LogDebug("Processing other video file {File}", file);

                    Either<BaseError, MediaItemScanResult<OtherVideo>> maybeVideo = await _otherVideoRepository
                        .GetOrAdd(libraryPath, knownFolder, file, cancellationToken)
                        .BindT(video => UpdateStatistics(video, ffmpegPath, ffprobePath))
                        .BindT(video => UpdateLibraryFolderId(video, knownFolder))
                        .BindT(UpdateMetadata)
                        .BindT(video => UpdateThumbnail(video, cancellationToken))
                        .BindT(result => UpdateSubtitles(result, cancellationToken))
                        .BindT(result => UpdateChapters(result, cancellationToken))
                        .BindT(FlagNormal);

                    foreach (BaseError error in maybeVideo.LeftToSeq())
                    {
                        _logger.LogWarning("Error processing other video at {Path}: {Error}", file, error.Value);
                        hasErrors = true;
                    }

                    foreach (MediaItemScanResult<OtherVideo> result in maybeVideo.RightToSeq())
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
                    await _libraryRepository.SetEtag(libraryPath, knownFolder, otherVideoFolder, etag);
                }
            }

            foreach (string path in await _otherVideoRepository.FindOtherVideoPaths(libraryPath))
            {
                if (!_fileSystem.File.Exists(path))
                {
                    _logger.LogInformation("Flagging missing other video at {Path}", path);
                    List<int> otherVideoIds = await FlagFileNotFound(libraryPath, path);
                    if (!await _scannerProxy.ReindexMediaItems(otherVideoIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to reindex media items from scanner process");
                    }

                }
                else if (Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Removing dot underscore file at {Path}", path);
                    List<int> otherVideoIds = await _otherVideoRepository.DeleteByPath(libraryPath, path);
                    if (!await _scannerProxy.RemoveMediaItems(otherVideoIds.ToArray(), cancellationToken))
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

    private async Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> UpdateLibraryFolderId(
        MediaItemScanResult<OtherVideo> video,
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

    private async Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> UpdateMetadata(
        MediaItemScanResult<OtherVideo> result)
    {
        try
        {
            OtherVideo otherVideo = result.Item;
            string path = otherVideo.MediaVersions.Head().MediaFiles.Head().Path;

            Option<string> maybeNfoFile = new List<string> { Path.ChangeExtension(path, "nfo") }
                .Filter(_fileSystem.File.Exists)
                .HeadOrNone();

            if (maybeNfoFile.IsNone)
            {
                if (!Optional(otherVideo.OtherVideoMetadata).Flatten().Any())
                {
                    _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", path);
                    if (await _localMetadataProvider.RefreshFallbackMetadata(otherVideo))
                    {
                        result.IsUpdated = true;
                    }
                }
            }

            foreach (string nfoFile in maybeNfoFile)
            {
                bool shouldUpdate = Optional(otherVideo.OtherVideoMetadata).Flatten().HeadOrNone().Match(
                    m => m.MetadataKind == MetadataKind.Fallback ||
                         m.DateUpdated != _localFileSystem.GetLastWriteTime(nfoFile),
                    true);

                if (shouldUpdate)
                {
                    _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                    if (await _localMetadataProvider.RefreshSidecarMetadata(otherVideo, nfoFile))
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

    private async Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> UpdateSubtitles(
        MediaItemScanResult<OtherVideo> result,
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

    private async Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> UpdateChapters(
        MediaItemScanResult<OtherVideo> result,
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

    private async Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> UpdateThumbnail(
        MediaItemScanResult<OtherVideo> result,
        CancellationToken cancellationToken)
    {
        try
        {
            OtherVideo otherVideo = result.Item;

            foreach (OtherVideoMetadata metadata in otherVideo.OtherVideoMetadata.HeadOrNone())
            {
                Option<string> maybeThumbnail = LocateThumbnail(otherVideo);
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

    private Option<string> LocateThumbnail(OtherVideo otherVideo)
    {
        string path = otherVideo.MediaVersions.Head().MediaFiles.Head().Path;
        return ImageFileExtensions
            .Map(ext => Path.ChangeExtension(path, ext))
            .Filter(f => _fileSystem.File.Exists(f))
            .HeadOrNone();
    }
}

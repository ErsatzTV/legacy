using System.Collections.Immutable;
using System.IO.Abstractions;
using CliWrap;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Scanner.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Scanner.Core.Metadata;

public abstract class LocalFolderScanner
{
    public static readonly ImmutableHashSet<string> VideoFileExtensions = new[]
    {
        ".avs", ".mpg", ".mp2", ".mpeg", ".mpe", ".mpv", ".ogg", ".ogv", ".mp4",
        ".m4p", ".m4v", ".avi", ".wmv", ".mov", ".mkv", ".m2ts", ".ts", ".webm"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly ImmutableHashSet<string> AudioFileExtensions = new[]
    {
        ".aac", ".aif", ".aifc", ".aiff", ".alac", ".dff", ".dsf", ".flac", ".mp3",
        ".m4a", ".ogg", ".opus", ".oga", ".ogx", ".spx", ".wav", ".wma"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly ImmutableHashSet<string> ImageFileExtensions = new[]
    {
        "jpg", "jpeg", "png", "gif", "tbn", "webp"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly ImmutableHashSet<string> ExtraFiles = new[]
    {
        "behindthescenes", "deleted", "featurette",
        "interview", "scene", "short", "trailer", "other"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly ImmutableHashSet<string> ExtraDirectories = new List<string>
        {
            "behind the scenes", "deleted scenes", "featurettes",
            "interviews", "scenes", "shorts", "trailers", "other",
            "extras", "specials"
        }
        .Map(s => $"{Path.DirectorySeparatorChar}{s}{Path.DirectorySeparatorChar}")
        .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public static readonly ImmutableHashSet<string> RemoteStreamExtensions = new[]
    {
        "yml"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly IFFmpegPngService _ffmpegPngService;

    private readonly IImageCache _imageCache;

    private readonly IFileSystem _fileSystem;
    private readonly ILocalStatisticsProvider _localStatisticsProvider;
    private readonly ILogger _logger;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly IMetadataRepository _metadataRepository;
    private readonly ITempFilePool _tempFilePool;

    protected LocalFolderScanner(
        IFileSystem fileSystem,
        ILocalStatisticsProvider localStatisticsProvider,
        IMetadataRepository metadataRepository,
        IMediaItemRepository mediaItemRepository,
        IImageCache imageCache,
        IFFmpegPngService ffmpegPngService,
        ITempFilePool tempFilePool,
        ILogger logger)
    {
        _fileSystem = fileSystem;
        _localStatisticsProvider = localStatisticsProvider;
        _metadataRepository = metadataRepository;
        _mediaItemRepository = mediaItemRepository;
        _imageCache = imageCache;
        _ffmpegPngService = ffmpegPngService;
        _tempFilePool = tempFilePool;
        _logger = logger;
    }

    protected async Task<Either<BaseError, MediaItemScanResult<T>>> UpdateStatistics<T>(
        MediaItemScanResult<T> mediaItem,
        string ffmpegPath,
        string ffprobePath)
        where T : MediaItem
    {
        try
        {
            MediaVersion version = mediaItem.Item.GetHeadVersion();

            string path = version.MediaFiles.Head().Path;

            if (version.DateUpdated != _fileSystem.File.GetLastWriteTime(path) || version.Streams.Count == 0)
            {
                _logger.LogDebug("Refreshing {Attribute} for {Path}", "Statistics", path);
                Either<BaseError, bool> refreshResult =
                    await _localStatisticsProvider.RefreshStatistics(ffmpegPath, ffprobePath, mediaItem.Item);

                foreach (BaseError error in refreshResult.LeftToSeq())
                {
                    _logger.LogWarning(
                        "Unable to refresh {Attribute} for media item {Path}. Error: {Error}",
                        "Statistics",
                        path,
                        error.Value);
                }

                foreach (bool result in refreshResult.RightToSeq())
                {
                    if (result)
                    {
                        mediaItem.IsUpdated = true;
                    }
                }
            }

            return mediaItem;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }

    protected async Task<bool> RefreshArtwork(
        string artworkFile,
        ErsatzTV.Core.Domain.Metadata metadata,
        ArtworkKind artworkKind,
        Option<string> ffmpegPath,
        Option<int> attachedPicIndex,
        CancellationToken cancellationToken)
    {
        DateTime lastWriteTime = _fileSystem.File.GetLastWriteTime(artworkFile);

        metadata.Artwork ??= [];

        Option<Artwork> maybeArtwork = metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == artworkKind);

        bool cacheMissing = maybeArtwork.Match(a => !_imageCache.IsCached(a.Path, artworkKind), false);

        bool shouldRefresh = maybeArtwork.Match(
            artwork => cacheMissing || lastWriteTime.Subtract(artwork.DateUpdated) > TimeSpan.FromSeconds(1),
            true);

        if (shouldRefresh)
        {
            try
            {
                _logger.LogDebug("Refreshing {Attribute} from {Path}", artworkKind, artworkFile);

                string sourcePath = artworkFile;
                if (!cacheMissing && await _metadataRepository.CloneArtwork(
                        metadata,
                        maybeArtwork,
                        artworkKind,
                        sourcePath,
                        lastWriteTime))
                {
                    return true;
                }

                // if ffmpeg path is passed, we need pre-processing
                foreach (string path in ffmpegPath)
                {
                    artworkFile = await attachedPicIndex.Match(
                        async picIndex =>
                        {
                            // extract attached pic (and convert to png)
                            string tempName = _tempFilePool.GetNextTempFile(TempFileCategory.CoverArt);
                            Command process = _ffmpegPngService.ExtractAttachedPicAsPng(
                                path,
                                artworkFile,
                                picIndex,
                                tempName);

                            await process.ExecuteAsync(cancellationToken);

                            return tempName;
                        },
                        async () =>
                        {
                            // no attached pic index means convert to png
                            string tempName = _tempFilePool.GetNextTempFile(TempFileCategory.CoverArt);
                            Command process = _ffmpegPngService.ConvertToPng(path, artworkFile, tempName);

                            await process.ExecuteAsync(cancellationToken);

                            return tempName;
                        });
                }

                Either<BaseError, string> maybeCacheName =
                    await _imageCache.CopyArtworkToCache(artworkFile, artworkKind);

                return await maybeCacheName.Match(
                    async cacheName =>
                    {
                        await maybeArtwork.Match(
                            async artwork =>
                            {
                                artwork.Path = cacheName;
                                artwork.SourcePath = sourcePath;
                                artwork.DateUpdated = lastWriteTime;

                                if (metadata is SongMetadata)
                                {
                                    artwork.BlurHash43 = await _imageCache.CalculateBlurHash(
                                        cacheName,
                                        artworkKind,
                                        4,
                                        3);
                                    artwork.BlurHash54 = await _imageCache.CalculateBlurHash(
                                        cacheName,
                                        artworkKind,
                                        5,
                                        4);
                                    artwork.BlurHash64 = await _imageCache.CalculateBlurHash(
                                        cacheName,
                                        artworkKind,
                                        6,
                                        4);
                                }

                                await _metadataRepository.UpdateArtworkPath(artwork);
                            },
                            async () =>
                            {
                                var artwork = new Artwork
                                {
                                    Path = cacheName,
                                    SourcePath = sourcePath,
                                    DateAdded = DateTime.UtcNow,
                                    DateUpdated = lastWriteTime,
                                    ArtworkKind = artworkKind
                                };

                                if (metadata is SongMetadata)
                                {
                                    artwork.BlurHash43 = await _imageCache.CalculateBlurHash(
                                        cacheName,
                                        artworkKind,
                                        4,
                                        3);
                                    artwork.BlurHash54 = await _imageCache.CalculateBlurHash(
                                        cacheName,
                                        artworkKind,
                                        5,
                                        4);
                                    artwork.BlurHash64 = await _imageCache.CalculateBlurHash(
                                        cacheName,
                                        artworkKind,
                                        6,
                                        4);
                                }

                                metadata.Artwork.Add(artwork);
                                await _metadataRepository.AddArtwork(metadata, artwork);
                            });

                        return true;
                    },
                    error =>
                    {
                        _logger.LogDebug("Failed to cache artwork from {Path}: {Error}", artworkFile, error.Value);
                        return Task.FromResult(false);
                    });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error refreshing artwork");
            }
        }

        return false;
    }

    protected Task<List<int>> FlagFileNotFound(LibraryPath libraryPath, string path) =>
        _mediaItemRepository.FlagFileNotFound(libraryPath, path);

    protected async Task<Either<BaseError, MediaItemScanResult<T>>> FlagNormal<T>(MediaItemScanResult<T> result)
        where T : MediaItem
    {
        try
        {
            T mediaItem = result.Item;
            if (mediaItem.State != MediaItemState.Normal)
            {
                await _mediaItemRepository.FlagNormal(mediaItem);
                result.IsUpdated = true;
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    protected bool ShouldIncludeFolder(string folder) =>
        !string.IsNullOrWhiteSpace(folder) &&
        !Path.GetFileName(folder).StartsWith('.') &&
        !_fileSystem.File.Exists(Path.Combine(folder, ".etvignore"));
}

using System.Collections.Immutable;
using System.Globalization;
using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Images;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Maintenance;

public class DeleteOrphanedArtworkHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IArtworkRepository artworkRepository,
    IFileSystem fileSystem,
    IImageCache imageCache,
    ILogger<DeleteOrphanedArtworkHandler> logger)
    : IRequestHandler<DeleteOrphanedArtwork, Either<BaseError, Unit>>
{
    private static readonly ImmutableHashSet<string> ImageFileExtensions = new[]
    {
        ".jpg", ".jpeg", ".png", ".gif", ".tbn", ".webp"
    }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<Either<BaseError, Unit>> Handle(
        DeleteOrphanedArtwork request,
        CancellationToken cancellationToken)
    {
        try
        {
            await CleanUpDatabase(request, cancellationToken);

            System.Collections.Generic.HashSet<string> cacheFiles = await CleanUpFileSystem(cancellationToken);

            await CacheMissingArtwork(cacheFiles, cancellationToken);

            return Unit.Default;
        }
        catch (Exception e)
        {
            return BaseError.New(e.Message);
        }
    }

    private async Task CleanUpDatabase(DeleteOrphanedArtwork request, CancellationToken cancellationToken)
    {
        // delete actors that no longer reference any metadata; otherwise the artwork
        // they point to is shielded from the orphaned artwork cleanup below
        int deletedActors = await artworkRepository.DeleteOrphanedActors(request.MaxToDelete, cancellationToken);
        if (deletedActors > 0)
        {
            logger.LogDebug("Deleted {Count} orphaned actors", deletedActors);
        }
        else
        {
            logger.LogDebug("No orphaned actors to delete");
        }

        int deletedArtwork = await artworkRepository.DeleteOrphanedArtwork(request.MaxToDelete, cancellationToken);
        if (deletedArtwork > 0)
        {
            logger.LogDebug("Deleted {Count} orphaned artwork", deletedArtwork);
        }
        else
        {
            logger.LogDebug("No orphaned artwork to delete");
        }
    }

    private async Task<System.Collections.Generic.HashSet<string>> CleanUpFileSystem(
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        System.Collections.Generic.HashSet<string> validFiles = [];
        System.Collections.Generic.HashSet<string> cacheFiles = [];

        List<string> watermarks = await dbContext.ChannelWatermarks
            .TagWithCallSite()
            .AsNoTracking()
            .Select(c => c.Image)
            .ToListAsync(cancellationToken);

        foreach (string watermark in watermarks.Where(w => !string.IsNullOrWhiteSpace(w)))
        {
            validFiles.Add(watermark);
        }

        var lastId = 0;
        while (true)
        {
            List<MinimalArtwork> result = await dbContext.Artwork
                .TagWithCallSite()
                .AsNoTracking()
                .Where(a => a.Id > lastId)
                .OrderBy(a => a.Id)
                .Take(1000)
                .Select(a => new MinimalArtwork(a.Id, a.Path, a.BlurHash43, a.BlurHash54, a.BlurHash64))
                .ToListAsync(cancellationToken);

            if (result.Count == 0)
            {
                break;
            }

            foreach (MinimalArtwork artwork in result)
            {
                if (!string.IsNullOrWhiteSpace(artwork.Path) && !artwork.Path.Contains('/'))
                {
                    validFiles.Add(artwork.Path);
                }

                if (!string.IsNullOrWhiteSpace(artwork.BlurHash43))
                {
                    validFiles.Add(ImageCache.GetBlurHashFileName(artwork.BlurHash43));
                }

                if (!string.IsNullOrWhiteSpace(artwork.BlurHash54))
                {
                    validFiles.Add(ImageCache.GetBlurHashFileName(artwork.BlurHash54));
                }

                if (!string.IsNullOrWhiteSpace(artwork.BlurHash64))
                {
                    validFiles.Add(ImageCache.GetBlurHashFileName(artwork.BlurHash64));
                }
            }

            lastId = result.Last().Id;
        }

        logger.LogDebug("Loaded {Count} artwork hashes (valid file names)", validFiles.Count);

        var deleted = 0;
        long bytes = 0;
        foreach (string file in fileSystem.Directory.EnumerateFiles(
                     FileSystemLayout.ArtworkCacheFolder,
                     "*.*",
                     SearchOption.AllDirectories))
        {
            string fileName = fileSystem.Path.GetFileName(file);
            if (!validFiles.Contains(fileName))
            {
                try
                {
                    bytes += fileSystem.FileInfo.New(file).Length;

                    fileSystem.File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not delete artwork file {File}", file);
                }
            }
            else
            {
                cacheFiles.Add(fileName);
            }
        }

        if (deleted > 0)
        {
            logger.LogDebug(
                "Deleted {Count} unused artwork cache files totaling {Size}",
                deleted,
                bytes.Bytes().Humanize(CultureInfo.CurrentCulture));
        }
        else
        {
            logger.LogDebug("No unused artwork cache files to delete");
        }

        DeleteEmptySubfolders(FileSystemLayout.ArtworkCacheFolder);

        return cacheFiles;
    }

    private void DeleteEmptySubfolders(string path)
    {
        if (!fileSystem.Directory.Exists(path))
        {
            return;
        }

        foreach (string sub in fileSystem.Directory.GetDirectories(path))
        {
            DeleteEmptySubfolders(sub);
        }

        if (!fileSystem.Directory.EnumerateFileSystemEntries(path).Any())
        {
            try
            {
                // don't delete artwork cache folder or its direct children
                if (path != FileSystemLayout.ArtworkCacheFolder)
                {
                    var parent = fileSystem.Directory.GetParent(path);
                    if (parent?.FullName != FileSystemLayout.ArtworkCacheFolder)
                    {
                        fileSystem.Directory.Delete(path);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete empty cache folder {Folder}", path);
            }
        }
    }

    private async Task CacheMissingArtwork(
        System.Collections.Generic.HashSet<string> cacheFiles,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // artwork is deduplicated by source, so one cache file can serve many rows
        System.Collections.Generic.HashSet<string> restoredPaths = [];

        var restored = 0;
        var unrestorable = 0;

        var lastId = 0;
        while (true)
        {
            List<MissingArtwork> result = await dbContext.Artwork
                .TagWithCallSite()
                .AsNoTracking()
                .Where(a => a.Id > lastId)
                .OrderBy(a => a.Id)
                .Take(1000)
                .Select(a => new MissingArtwork(a.Id, a.Path, a.ArtworkKind, a.SourcePath))
                .ToListAsync(cancellationToken);

            if (result.Count == 0)
            {
                break;
            }

            foreach (MissingArtwork artwork in result)
            {
                if (string.IsNullOrWhiteSpace(artwork.Path) || artwork.Path.Contains('/'))
                {
                    continue;
                }

                if (cacheFiles.Contains(artwork.Path) || restoredPaths.Contains(artwork.Path))
                {
                    continue;
                }

                // the source is a remote key for media server artwork, and the media file itself
                // for embedded cover art; neither one is an image to copy into the cache
                if (string.IsNullOrWhiteSpace(artwork.SourcePath) || !IsImageFile(artwork.SourcePath) ||
                    !fileSystem.File.Exists(artwork.SourcePath))
                {
                    unrestorable++;
                    continue;
                }

                logger.LogDebug(
                    "Restoring missing artwork {Path} from source {SourcePath}",
                    artwork.Path,
                    artwork.SourcePath);

                Either<BaseError, string> copyResult =
                    await imageCache.CopyArtworkToCache(artwork.SourcePath, artwork.Kind);

                foreach (BaseError error in copyResult.LeftToSeq())
                {
                    logger.LogWarning(
                        "Failed to restore artwork {Path} from source {SourcePath}: {Error}",
                        artwork.Path,
                        artwork.SourcePath,
                        error.Value);
                }

                foreach (string cacheName in copyResult.RightToSeq())
                {
                    restored++;
                    restoredPaths.Add(artwork.Path);
                    cacheFiles.Add(cacheName);

                    // the cache file name comes from the source path and its write time; rows
                    // written from an ffmpeg-converted copy of the source have a different name
                    if (!string.Equals(cacheName, artwork.Path, StringComparison.Ordinal))
                    {
                        string stalePath = artwork.Path;
                        int repointed = await dbContext.Artwork
                            .Where(a => a.Path == stalePath)
                            .ExecuteUpdateAsync(
                                s => s.SetProperty(a => a.Path, cacheName),
                                cancellationToken);

                        logger.LogDebug(
                            "Repointed {Count} artwork rows from {StalePath} to {Path}",
                            repointed,
                            stalePath,
                            cacheName);
                    }
                }
            }

            lastId = result.Last().Id;
        }

        if (restored > 0)
        {
            logger.LogDebug("Restored {Count} missing artwork files to the cache", restored);
        }

        if (unrestorable > 0)
        {
            logger.LogDebug("{Count} missing artwork files have no local source to restore from", unrestorable);
        }
    }

    private bool IsImageFile(string path) => ImageFileExtensions.Contains(fileSystem.Path.GetExtension(path));

    private sealed record MinimalArtwork(
        int Id,
        string Path,
        string BlurHash43,
        string BlurHash54,
        string BlurHash64);

    private sealed record MissingArtwork(int Id, string Path, ArtworkKind Kind, string SourcePath);
}

using System.Globalization;
using System.IO.Abstractions;
using ErsatzTV.Core;
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
    ILogger<DeleteOrphanedArtworkHandler> logger)
    : IRequestHandler<DeleteOrphanedArtwork, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteOrphanedArtwork request,
        CancellationToken cancellationToken)
    {
        try
        {
            await CleanUpDatabase(request, cancellationToken);

            // temporarily disabled since this is now scheduled
            //await CleanUpFileSystem(cancellationToken);

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

        logger.LogDebug("Done cleaning!");
    }

    private async Task CleanUpFileSystem(CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        System.Collections.Generic.HashSet<string> validFiles = [];

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
        }

        logger.LogDebug(
            "Deleted {Count} unused artwork cache files totaling {Size}",
            deleted,
            bytes.Bytes().Humanize(CultureInfo.CurrentCulture));

        DeleteEmptySubfolders(FileSystemLayout.ArtworkCacheFolder);
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

    private sealed record MinimalArtwork(int Id, string Path, string BlurHash43, string BlurHash54, string BlurHash64);
}

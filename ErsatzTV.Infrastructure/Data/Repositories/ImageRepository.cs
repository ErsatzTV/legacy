using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class ImageRepository : IImageRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ILogger<ImageRepository> _logger;

    public ImageRepository(IDbContextFactory<TvContext> dbContextFactory, ILogger<ImageRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<Either<BaseError, MediaItemScanResult<Image>>> GetOrAdd(
        LibraryPath libraryPath,
        LibraryFolder libraryFolder,
        string path,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<Image> maybeExisting = await dbContext.Images
            .AsNoTracking()
            .Include(i => i.ImageMetadata)
            .ThenInclude(ovm => ovm.Genres)
            .Include(i => i.ImageMetadata)
            .ThenInclude(ovm => ovm.Tags)
            .Include(i => i.ImageMetadata)
            .ThenInclude(ovm => ovm.Studios)
            .Include(i => i.ImageMetadata)
            .ThenInclude(ovm => ovm.Guids)
            .Include(i => i.ImageMetadata)
            .ThenInclude(ovm => ovm.Actors)
            .Include(i => i.ImageMetadata)
            .ThenInclude(ovm => ovm.Actors)
            .ThenInclude(a => a.Artwork)
            .Include(ov => ov.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(ov => ov.MediaVersions)
            .ThenInclude(ov => ov.MediaFiles)
            .Include(ov => ov.MediaVersions)
            .ThenInclude(ov => ov.Streams)
            .Include(ov => ov.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .OrderBy(i => i.MediaVersions.First().MediaFiles.First().Path)
            .SingleOrDefaultAsync(i => i.MediaVersions.First().MediaFiles.First().Path == path, cancellationToken);

        return await maybeExisting.Match(
            mediaItem =>
                Right<BaseError, MediaItemScanResult<Image>>(
                    new MediaItemScanResult<Image>(mediaItem) { IsAdded = false }).AsTask(),
            async () => await AddImage(dbContext, libraryPath.Id, libraryFolder.Id, path, cancellationToken));
    }

    public async Task<IEnumerable<string>> FindImagePaths(LibraryPath libraryPath)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QueryAsync<string>(
            @"SELECT MF.Path
                FROM MediaFile MF
                INNER JOIN MediaVersion MV on MF.MediaVersionId = MV.Id
                INNER JOIN Image O on MV.ImageId = O.Id
                INNER JOIN MediaItem MI on O.Id = MI.Id
                WHERE MI.LibraryPathId = @LibraryPathId",
            new { LibraryPathId = libraryPath.Id });
    }

    public async Task<List<int>> DeleteByPath(LibraryPath libraryPath, string path)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        List<int> ids = await dbContext.Connection.QueryAsync<int>(
            @"SELECT O.Id
            FROM Image O
            INNER JOIN MediaItem MI on O.Id = MI.Id
            INNER JOIN MediaVersion MV on O.Id = MV.ImageId
            INNER JOIN MediaFile MF on MV.Id = MF.MediaVersionId
            WHERE MI.LibraryPathId = @LibraryPathId AND MF.Path = @Path",
            new { LibraryPathId = libraryPath.Id, Path = path }).Map(result => result.ToList());

        foreach (int otherVideoId in ids)
        {
            Image otherVideo = await dbContext.Images.FindAsync(otherVideoId);
            if (otherVideo != null)
            {
                dbContext.Images.Remove(otherVideo);
            }
        }

        await dbContext.SaveChangesAsync();

        return ids;
    }

    public async Task<bool> AddTag(ImageMetadata metadata, Tag tag)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Tag (Name, ImageMetadataId, ExternalCollectionId, ExternalTypeId) VALUES (@Name, @MetadataId, @ExternalCollectionId, @ExternalTypeId)",
            new { tag.Name, MetadataId = metadata.Id, tag.ExternalCollectionId, tag.ExternalTypeId }).Map(result => result > 0);
    }

    private async Task<Either<BaseError, MediaItemScanResult<Image>>> AddImage(
        TvContext dbContext,
        int libraryPathId,
        int libraryFolderId,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await MediaItemRepository.MediaFileAlreadyExists(
                    path,
                    libraryPathId,
                    dbContext,
                    _logger,
                    cancellationToken))
            {
                return new MediaFileAlreadyExists();
            }

            var image = new Image
            {
                LibraryPathId = libraryPathId,
                MediaVersions =
                [
                    new MediaVersion
                    {
                        MediaFiles =
                        [
                            new MediaFile
                            {
                                Path = path, PathHash = PathUtils.GetPathHash(path), LibraryFolderId = libraryFolderId
                            }
                        ],
                        Streams = []
                    }
                ],
                TraktListItems = new List<TraktListItem>
                {
                    Capacity = 0
                }
            };

            await dbContext.Images.AddAsync(image, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(image).Reference(m => m.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(image.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<Image>(image) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }
}

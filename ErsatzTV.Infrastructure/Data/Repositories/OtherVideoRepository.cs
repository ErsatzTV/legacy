using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class OtherVideoRepository : IOtherVideoRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ILogger<OtherVideoRepository> _logger;

    public OtherVideoRepository(IDbContextFactory<TvContext> dbContextFactory, ILogger<OtherVideoRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> GetOrAdd(
        LibraryPath libraryPath,
        LibraryFolder libraryFolder,
        string path,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<OtherVideo> maybeExisting = await dbContext.OtherVideos
            .AsNoTracking()
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Genres)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Tags)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Studios)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Guids)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Actors)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Actors)
            .ThenInclude(a => a.Artwork)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Directors)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Writers)
            .Include(i => i.OtherVideoMetadata)
            .ThenInclude(ovm => ovm.Artwork)
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
                Right<BaseError, MediaItemScanResult<OtherVideo>>(
                    new MediaItemScanResult<OtherVideo>(mediaItem) { IsAdded = false }).AsTask(),
            async () => await AddOtherVideo(dbContext, libraryPath.Id, libraryFolder.Id, path, cancellationToken));
    }

    public async Task<IEnumerable<string>> FindOtherVideoPaths(LibraryPath libraryPath)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QueryAsync<string>(
            @"SELECT MF.Path
                FROM MediaFile MF
                INNER JOIN MediaVersion MV on MF.MediaVersionId = MV.Id
                INNER JOIN OtherVideo O on MV.OtherVideoId = O.Id
                INNER JOIN MediaItem MI on O.Id = MI.Id
                WHERE MI.LibraryPathId = @LibraryPathId",
            new { LibraryPathId = libraryPath.Id });
    }

    public async Task<List<int>> DeleteByPath(LibraryPath libraryPath, string path)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        List<int> ids = await dbContext.Connection.QueryAsync<int>(
            @"SELECT O.Id
            FROM OtherVideo O
            INNER JOIN MediaItem MI on O.Id = MI.Id
            INNER JOIN MediaVersion MV on O.Id = MV.OtherVideoId
            INNER JOIN MediaFile MF on MV.Id = MF.MediaVersionId
            WHERE MI.LibraryPathId = @LibraryPathId AND MF.Path = @Path",
            new { LibraryPathId = libraryPath.Id, Path = path }).Map(result => result.ToList());

        foreach (int otherVideoId in ids)
        {
            OtherVideo otherVideo = await dbContext.OtherVideos.FindAsync(otherVideoId);
            if (otherVideo != null)
            {
                dbContext.OtherVideos.Remove(otherVideo);
            }
        }

        await dbContext.SaveChangesAsync();

        return ids;
    }

    public async Task<bool> AddGenre(OtherVideoMetadata metadata, Genre genre)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Genre (Name, OtherVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { genre.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddTag(OtherVideoMetadata metadata, Tag tag)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Tag (Name, OtherVideoMetadataId, ExternalCollectionId) VALUES (@Name, @MetadataId, @ExternalCollectionId)",
            new { tag.Name, MetadataId = metadata.Id, tag.ExternalCollectionId }).Map(result => result > 0);
    }

    public async Task<bool> AddStudio(OtherVideoMetadata metadata, Studio studio)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Studio (Name, OtherVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { studio.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddActor(OtherVideoMetadata metadata, Actor actor)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        int? artworkId = null;

        if (actor.Artwork != null)
        {
            artworkId = await dbContext.Connection.QuerySingleAsync<int>(
                $"""
                 INSERT INTO Artwork (ArtworkKind, DateAdded, DateUpdated, Path)
                     VALUES (@ArtworkKind, @DateAdded, @DateUpdated, @Path);
                 SELECT {TvContext.LastInsertedRowId}
                 """,
                new
                {
                    ArtworkKind = (int)actor.Artwork.ArtworkKind,
                    actor.Artwork.DateAdded,
                    actor.Artwork.DateUpdated,
                    actor.Artwork.Path
                });
        }

        return await dbContext.Connection.ExecuteAsync(
                "INSERT INTO Actor (Name, Role, `Order`, OtherVideoMetadataId, ArtworkId) VALUES (@Name, @Role, @Order, @MetadataId, @ArtworkId)",
                new { actor.Name, actor.Role, actor.Order, MetadataId = metadata.Id, ArtworkId = artworkId })
            .Map(result => result > 0);
    }

    public async Task<bool> AddDirector(OtherVideoMetadata metadata, Director director)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Director (Name, OtherVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { director.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddWriter(OtherVideoMetadata metadata, Writer writer)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Writer (Name, OtherVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { writer.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> UpdateTitles(OtherVideoMetadata metadata, string title, string sortTitle)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE OtherVideoMetadata SET Title = @Title, SortTitle = @SortTitle WHERE Id = @Id",
            new { Title = title, SortTitle = sortTitle, metadata.Id }).Map(result => result > 0);
    }

    private async Task<Either<BaseError, MediaItemScanResult<OtherVideo>>> AddOtherVideo(
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

            var otherVideo = new OtherVideo
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
                TraktListItems = []
            };

            await dbContext.OtherVideos.AddAsync(otherVideo, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(otherVideo).Reference(m => m.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(otherVideo.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<OtherVideo>(otherVideo) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }
}

using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class MusicVideoRepository : IMusicVideoRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public MusicVideoRepository(IDbContextFactory<TvContext> dbContextFactory) => _dbContextFactory = dbContextFactory;

    public async Task<Either<BaseError, MediaItemScanResult<MusicVideo>>> GetOrAdd(
        Artist artist,
        LibraryPath libraryPath,
        LibraryFolder libraryFolder,
        string path)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        Option<MusicVideo> maybeExisting = await dbContext.MusicVideos
            .AsNoTracking()
            .Include(mv => mv.Artist)
            .ThenInclude(a => a.ArtistMetadata)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Artwork)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Artists)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Genres)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Tags)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Studios)
            .Include(mv => mv.MusicVideoMetadata)
            .ThenInclude(mvm => mvm.Directors)
            .Include(mv => mv.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(mv => mv.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(mv => mv.MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(mv => mv.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .OrderBy(i => i.MediaVersions.First().MediaFiles.First().Path)
            .SingleOrDefaultAsync(i => i.MediaVersions.First().MediaFiles.First().Path == path);

        return await maybeExisting.Match(
            async mediaItem =>
            {
                if (mediaItem.ArtistId != artist.Id)
                {
                    await dbContext.Connection.ExecuteAsync(
                        @"UPDATE MusicVideo SET ArtistId = @ArtistId WHERE Id = @Id",
                        new { mediaItem.Id, ArtistId = artist.Id });

                    mediaItem.ArtistId = artist.Id;
                    mediaItem.Artist = artist;
                }

                return Right<BaseError, MediaItemScanResult<MusicVideo>>(
                    new MediaItemScanResult<MusicVideo>(mediaItem) { IsAdded = false });
            },
            async () => await AddMusicVideo(dbContext, artist, libraryPath.Id, libraryFolder.Id, path));
    }

    public async Task<IEnumerable<string>> FindMusicVideoPaths(LibraryPath libraryPath)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QueryAsync<string>(
            @"SELECT MF.Path
                FROM MediaFile MF
                INNER JOIN MediaVersion MV on MF.MediaVersionId = MV.Id
                INNER JOIN MusicVideo M on MV.MusicVideoId = M.Id
                INNER JOIN MediaItem MI on M.Id = MI.Id
                WHERE MI.LibraryPathId = @LibraryPathId",
            new { LibraryPathId = libraryPath.Id });
    }

    public async Task<List<int>> DeleteByPath(LibraryPath libraryPath, string path)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        List<int> ids = await dbContext.Connection.QueryAsync<int>(
            @"SELECT M.Id
                FROM MusicVideo M
                INNER JOIN MediaItem MI on M.Id = MI.Id
                INNER JOIN MediaVersion MV on M.Id = MV.MusicVideoId
                INNER JOIN MediaFile MF on MV.Id = MF.MediaVersionId
                WHERE MI.LibraryPathId = @LibraryPathId AND MF.Path = @Path",
            new { LibraryPathId = libraryPath.Id, Path = path }).Map(result => result.ToList());

        foreach (int musicVideoId in ids)
        {
            MusicVideo musicVideo = await dbContext.MusicVideos.FindAsync(musicVideoId);
            if (musicVideo != null)
            {
                dbContext.MusicVideos.Remove(musicVideo);
            }
        }

        await dbContext.SaveChangesAsync();

        return ids;
    }

    public async Task<bool> AddArtist(MusicVideoMetadata metadata, MusicVideoArtist artist)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO MusicVideoArtist (Name, MusicVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { artist.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddGenre(MusicVideoMetadata metadata, Genre genre)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Genre (Name, MusicVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { genre.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddTag(MusicVideoMetadata metadata, Tag tag)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Tag (Name, MusicVideoMetadataId, ExternalCollectionId, ExternalTypeId) VALUES (@Name, @MetadataId, @ExternalCollectionId, @ExternalTypeId)",
            new { tag.Name, MetadataId = metadata.Id, tag.ExternalCollectionId, tag.ExternalTypeId }).Map(result => result > 0);
    }

    public async Task<bool> AddStudio(MusicVideoMetadata metadata, Studio studio)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Studio (Name, MusicVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { studio.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> AddDirector(MusicVideoMetadata metadata, Director director)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "INSERT INTO Director (Name, MusicVideoMetadataId) VALUES (@Name, @MetadataId)",
            new { director.Name, MetadataId = metadata.Id }).Map(result => result > 0);
    }

    public async Task<bool> RemoveArtist(MusicVideoArtist artist)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
                "DELETE FROM MusicVideoArtist WHERE Id = @Id",
                new { artist.Id })
            .Map(result => result > 0);
    }

    public async Task<IEnumerable<string>> FindOrphanPaths(LibraryPath libraryPath)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QueryAsync<string>(
            @"SELECT MF.Path
                FROM MediaFile MF
                INNER JOIN MediaVersion MV on MF.MediaVersionId = MV.Id
                INNER JOIN MusicVideo M on MV.MusicVideoId = M.Id
                INNER JOIN MediaItem MI on M.Id = MI.Id
                WHERE MI.LibraryPathId = @LibraryPathId
                  AND NOT EXISTS (SELECT * FROM MusicVideoMetadata WHERE MusicVideoId = M.Id)",
            new { LibraryPathId = libraryPath.Id });
    }

    public async Task<int> GetMusicVideoCount(int artistId)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.QuerySingleAsync<int>(
            @"SELECT COUNT(*) FROM MusicVideo WHERE ArtistId = @ArtistId",
            new { ArtistId = artistId });
    }

    public async Task<List<MusicVideoMetadata>> GetPagedMusicVideos(int artistId, int pageNumber, int pageSize)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.MusicVideoMetadata
            .AsNoTracking()
            .Include(m => m.Artwork)
            .Include(m => m.Genres)
            .Include(m => m.Tags)
            .Include(m => m.Studios)
            .Include(m => m.Directors)
            .Include(m => m.MusicVideo)
            .ThenInclude(mv => mv.Artist)
            .ThenInclude(a => a.ArtistMetadata)
            .Include(m => m.MusicVideo)
            .ThenInclude(mv => mv.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Filter(m => m.MusicVideo.ArtistId == artistId)
            .OrderBy(m => m.SortTitle)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    private static async Task<Either<BaseError, MediaItemScanResult<MusicVideo>>> AddMusicVideo(
        TvContext dbContext,
        Artist artist,
        int libraryPathId,
        int libraryFolderId,
        string path)
    {
        try
        {
            var musicVideo = new MusicVideo
            {
                ArtistId = artist.Id,
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

            await dbContext.MusicVideos.AddAsync(musicVideo);
            await dbContext.SaveChangesAsync();
            await dbContext.Entry(musicVideo).Reference(m => m.Artist).LoadAsync();
            await dbContext.Entry(musicVideo.Artist).Collection(a => a.ArtistMetadata).LoadAsync();
            await dbContext.Entry(musicVideo).Reference(m => m.LibraryPath).LoadAsync();
            await dbContext.Entry(musicVideo.LibraryPath).Reference(lp => lp.Library).LoadAsync();
            return new MediaItemScanResult<MusicVideo>(musicVideo) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }
}

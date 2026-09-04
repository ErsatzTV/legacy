using Dapper;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class PlexCollectionRepository : IPlexCollectionRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public PlexCollectionRepository(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<List<PlexCollection>> GetCollections()
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.PlexCollections.ToListAsync();
    }

    public async Task<bool> AddCollection(PlexCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        await dbContext.AddAsync(collection);
        return await dbContext.SaveChangesAsync() > 0;
    }

    public async Task<bool> RemoveCollection(PlexCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        dbContext.Remove(collection);

        // remove all tags that reference this collection
        await dbContext.Connection.ExecuteAsync(
            @"DELETE FROM Tag WHERE ExternalCollectionId = @Key AND ExternalTypeId = @TagTypeId",
            new { collection.Key, TagTypeId = Tag.PlexCollectionTypeId });

        return await dbContext.SaveChangesAsync() > 0;
    }

    public async Task<List<int>> RemoveAllTags(PlexCollection collection)
    {
        var result = new List<int>();

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        // movies
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT JM.Id FROM Tag T
              INNER JOIN MovieMetadata MM on T.MovieMetadataId = MM.Id
              INNER JOIN PlexMovie JM on JM.Id = MM.MovieId
              WHERE T.ExternalCollectionId = @Key AND T.ExternalTypeId = @TagTypeId",
                new { collection.Key, TagTypeId = Tag.PlexCollectionTypeId }));

        // shows
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT JS.Id FROM Tag T
              INNER JOIN ShowMetadata SM on T.ShowMetadataId = SM.Id
              INNER JOIN PlexShow JS on JS.Id = SM.ShowId
              WHERE T.ExternalCollectionId = @Key AND T.ExternalTypeId = @TagTypeId",
                new { collection.Key, TagTypeId = Tag.PlexCollectionTypeId }));

        // seasons
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT JS.Id FROM Tag T
              INNER JOIN SeasonMetadata SM on T.SeasonMetadataId = SM.Id
              INNER JOIN PlexSeason JS on JS.Id = SM.SeasonId
              WHERE T.ExternalCollectionId = @Key AND T.ExternalTypeId = @TagTypeId",
                new { collection.Key, TagTypeId = Tag.PlexCollectionTypeId }));

        // episodes
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT JE.Id FROM Tag T
              INNER JOIN EpisodeMetadata EM on T.EpisodeMetadataId = EM.Id
              INNER JOIN PlexEpisode JE on JE.Id = EM.EpisodeId
              WHERE T.ExternalCollectionId = @Key AND T.ExternalTypeId = @TagTypeId",
                new { collection.Key, TagTypeId = Tag.PlexCollectionTypeId }));

        // delete all tags
        await dbContext.Connection.ExecuteAsync(
            @"DELETE FROM Tag WHERE ExternalCollectionId = @Key AND ExternalTypeId = @TagTypeId",
            new { collection.Key, TagTypeId = Tag.PlexCollectionTypeId });

        return result;
    }

    public async Task<int> AddTag(MediaItem item, PlexCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        switch (item)
        {
            case PlexMovie movie:
                int movieId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM PlexMovie WHERE `Key` = @Key",
                    new { movie.Key });
                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, MovieMetadataId)
                      SELECT @Name, @Key, @TagTypeId, Id FROM
                      (SELECT Id FROM MovieMetadata WHERE MovieId = @MovieId) AS A",
                    new { collection.Name, collection.Key, TagTypeId = Tag.PlexCollectionTypeId, MovieId = movieId });
                return movieId;
            case PlexShow show:
                int showId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM PlexShow WHERE `Key` = @Key",
                    new { show.Key });
                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, ShowMetadataId)
                      SELECT @Name, @Key, @TagTypeId, Id FROM
                      (SELECT Id FROM ShowMetadata WHERE ShowId = @ShowId) AS A",
                    new { collection.Name, collection.Key, TagTypeId = Tag.PlexCollectionTypeId, ShowId = showId });
                return showId;
            case PlexSeason season:
                int seasonId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM PlexSeason WHERE `Key` = @Key",
                    new { season.Key });
                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, SeasonMetadataId)
                      SELECT @Name, @Key, @TagTypeId, Id FROM
                      (SELECT Id FROM SeasonMetadata WHERE SeasonId = @SeasonId) AS A",
                    new { collection.Name, collection.Key, TagTypeId = Tag.PlexCollectionTypeId, SeasonId = seasonId });
                return seasonId;
            case PlexEpisode episode:
                int episodeId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM PlexEpisode WHERE `Key` = @Key",
                    new { episode.Key });
                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, EpisodeMetadataId)
                      SELECT @Name, @Key, @TagTypeId, Id FROM
                      (SELECT Id FROM EpisodeMetadata WHERE EpisodeId = @EpisodeId) AS A",
                    new { collection.Name, collection.Key, TagTypeId = Tag.PlexCollectionTypeId, EpisodeId = episodeId });
                return episodeId;
            default:
                return 0;
        }
    }

    public async Task<bool> SetEtag(PlexCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            @"UPDATE PlexCollection SET Etag = @Etag WHERE `Key` = @Key",
            new { collection.Etag, collection.Key }) > 0;
    }
}

using Dapper;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class EmbyCollectionRepository : IEmbyCollectionRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public EmbyCollectionRepository(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<List<EmbyCollection>> GetCollections()
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.EmbyCollections.ToListAsync();
    }

    public async Task<bool> AddCollection(EmbyCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        await dbContext.AddAsync(collection);
        return await dbContext.SaveChangesAsync() > 0;
    }

    public async Task<bool> RemoveCollection(EmbyCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        dbContext.Remove(collection);

        // remove all tags that reference this collection
        await dbContext.Connection.ExecuteAsync(
            @"DELETE FROM Tag WHERE ExternalCollectionId = @ItemId AND ExternalTypeId = @TagTypeId",
            new { collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId });

        return await dbContext.SaveChangesAsync() > 0;
    }

    public async Task<List<int>> RemoveAllTags(EmbyCollection collection)
    {
        var result = new List<int>();

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();

        // movies
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT EM.Id FROM Tag T
              INNER JOIN MovieMetadata MM on T.MovieMetadataId = MM.Id
              INNER JOIN EmbyMovie EM on EM.Id = MM.MovieId
              WHERE T.ExternalCollectionId = @ItemId AND T.ExternalTypeId = @TagTypeId",
                new { collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId }));

        // shows
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT ES.Id FROM Tag T
              INNER JOIN ShowMetadata SM on T.ShowMetadataId = SM.Id
              INNER JOIN EmbyShow ES on ES.Id = SM.ShowId
              WHERE T.ExternalCollectionId = @ItemId AND T.ExternalTypeId = @TagTypeId",
                new { collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId }));

        // seasons
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT ES.Id FROM Tag T
              INNER JOIN SeasonMetadata SM on T.SeasonMetadataId = SM.Id
              INNER JOIN EmbySeason ES on ES.Id = SM.SeasonId
              WHERE T.ExternalCollectionId = @ItemId AND T.ExternalTypeId = @TagTypeId",
                new { collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId }));

        // episodes
        result.AddRange(
            await dbContext.Connection.QueryAsync<int>(
                @"SELECT EE.Id FROM Tag T
              INNER JOIN EpisodeMetadata EM on T.EpisodeMetadataId = EM.Id
              INNER JOIN EmbyEpisode EE on EE.Id = EM.EpisodeId
              WHERE T.ExternalCollectionId = @ItemId AND T.ExternalTypeId = @TagTypeId",
                new { collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId }));

        // delete all tags
        await dbContext.Connection.ExecuteAsync(
            @"DELETE FROM Tag WHERE ExternalCollectionId = @ItemId AND ExternalTypeId = @TagTypeId",
            new { collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId });

        return result;
    }

    public async Task<Option<int>> AddTag(MediaItem item, EmbyCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        switch (item)
        {
            case EmbyMovie movie:
                int movieId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM EmbyMovie WHERE ItemId = @ItemId",
                    new { movie.ItemId });
                if (movieId <= 0)
                {
                    return Option<int>.None;
                }

                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, MovieMetadataId)
                      SELECT @Name, @ItemId, @TagTypeId, Id FROM
                      (SELECT Id FROM MovieMetadata WHERE MovieId = @MovieId) AS A",
                    new { collection.Name, collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId, MovieId = movieId });
                return movieId;
            case EmbyShow show:
                int showId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM EmbyShow WHERE ItemId = @ItemId",
                    new { show.ItemId });
                if (showId <= 0)
                {
                    return Option<int>.None;
                }

                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, ShowMetadataId)
                      SELECT @Name, @ItemId, @TagTypeId, Id FROM
                      (SELECT Id FROM ShowMetadata WHERE ShowId = @ShowId) AS A",
                    new { collection.Name, collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId, ShowId = showId });
                return showId;
            case EmbySeason season:
                int seasonId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM EmbySeason WHERE ItemId = @ItemId",
                    new { season.ItemId });
                if (seasonId <= 0)
                {
                    return Option<int>.None;
                }

                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, SeasonMetadataId)
                      SELECT @Name, @ItemId, @TagTypeId, Id FROM
                      (SELECT Id FROM SeasonMetadata WHERE SeasonId = @SeasonId) AS A",
                    new { collection.Name, collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId, SeasonId = seasonId });
                return seasonId;
            case EmbyEpisode episode:
                int episodeId = await dbContext.Connection.ExecuteScalarAsync<int>(
                    @"SELECT Id FROM EmbyEpisode WHERE ItemId = @ItemId",
                    new { episode.ItemId });
                if (episodeId <= 0)
                {
                    return Option<int>.None;
                }

                await dbContext.Connection.ExecuteAsync(
                    @"INSERT INTO Tag (Name, ExternalCollectionId, ExternalTypeId, EpisodeMetadataId)
                      SELECT @Name, @ItemId, @TagTypeId, Id FROM
                      (SELECT Id FROM EpisodeMetadata WHERE EpisodeId = @EpisodeId) AS A",
                    new { collection.Name, collection.ItemId, TagTypeId = Tag.EmbyCollectionTypeId, EpisodeId = episodeId });
                return episodeId;
            default:
                return Option<int>.None;
        }
    }

    public async Task<bool> SetEtag(EmbyCollection collection)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            @"UPDATE EmbyCollection SET Etag = @Etag WHERE ItemId = @ItemId",
            new { collection.Etag, collection.ItemId }) > 0;
    }
}

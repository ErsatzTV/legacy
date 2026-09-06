using System.Globalization;
using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Plex;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class PlexTelevisionRepository : IPlexTelevisionRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ILogger<PlexTelevisionRepository> _logger;

    public PlexTelevisionRepository(
        IDbContextFactory<TvContext> dbContextFactory,
        ILogger<PlexTelevisionRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<Option<int>> FlagNormal(
        PlexLibrary library,
        PlexEpisode episode,
        CancellationToken cancellationToken)
    {
        if (episode.State is MediaItemState.Normal)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        episode.State = MediaItemState.Normal;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT PlexEpisode.Id FROM PlexEpisode
                    INNER JOIN MediaItem MI ON MI.Id = PlexEpisode.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE PlexEpisode.Key = @Key",
                parameters: new { LibraryId = library.Id, episode.Key },
                cancellationToken: cancellationToken));

        foreach (int id in maybeId)
        {
            return await dbContext.Connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE MediaItem SET State = 0 WHERE Id = @Id AND State != 0",
                    parameters: new { Id = id },
                    cancellationToken: cancellationToken)).Map(count => count > 0 ? Some(id) : None);
        }

        return None;
    }

    public async Task<Option<int>> FlagNormal(
        PlexLibrary library,
        PlexSeason season,
        CancellationToken cancellationToken)
    {
        if (season.State is MediaItemState.Normal)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        season.State = MediaItemState.Normal;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT PlexSeason.Id FROM PlexSeason
                    INNER JOIN MediaItem MI ON MI.Id = PlexSeason.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE PlexSeason.Key = @Key",
                parameters: new { LibraryId = library.Id, season.Key },
                cancellationToken: cancellationToken));

        foreach (int id in maybeId)
        {
            return await dbContext.Connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE MediaItem SET State = 0 WHERE Id = @Id AND State != 0",
                    parameters: new { Id = id },
                    cancellationToken: cancellationToken)).Map(count => count > 0 ? Some(id) : None);
        }

        return None;
    }

    public async Task<Option<int>> FlagNormal(PlexLibrary library, PlexShow show, CancellationToken cancellationToken)
    {
        if (show.State is MediaItemState.Normal)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        show.State = MediaItemState.Normal;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT PlexShow.Id FROM PlexShow
                    INNER JOIN MediaItem MI ON MI.Id = PlexShow.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE PlexShow.Key = @Key",
                parameters: new { LibraryId = library.Id, show.Key },
                cancellationToken: cancellationToken));

        foreach (int id in maybeId)
        {
            return await dbContext.Connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE MediaItem SET State = 0 WHERE Id = @Id AND State != 0",
                    parameters: new { Id = id },
                    cancellationToken: cancellationToken)).Map(count => count > 0 ? Some(id) : None);
        }

        return None;
    }

    public async Task<Option<int>> FlagUnavailable(
        PlexLibrary library,
        PlexEpisode episode,
        CancellationToken cancellationToken)
    {
        if (episode.State is MediaItemState.Unavailable)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        episode.State = MediaItemState.Unavailable;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT PlexEpisode.Id FROM PlexEpisode
                    INNER JOIN MediaItem MI ON MI.Id = PlexEpisode.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE PlexEpisode.Key = @Key",
                parameters: new { LibraryId = library.Id, episode.Key },
                cancellationToken: cancellationToken));

        foreach (int id in maybeId)
        {
            return await dbContext.Connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE MediaItem SET State = 2 WHERE Id = @Id AND State != 2",
                    parameters: new { Id = id },
                    cancellationToken: cancellationToken)).Map(count => count > 0 ? Some(id) : None);
        }

        return None;
    }

    public async Task<Option<int>> FlagRemoteOnly(
        PlexLibrary library,
        PlexEpisode episode,
        CancellationToken cancellationToken)
    {
        if (episode.State is MediaItemState.RemoteOnly)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        episode.State = MediaItemState.RemoteOnly;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT PlexEpisode.Id FROM PlexEpisode
                    INNER JOIN MediaItem MI ON MI.Id = PlexEpisode.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE PlexEpisode.Key = @Key",
                parameters: new { LibraryId = library.Id, episode.Key },
                cancellationToken: cancellationToken));

        foreach (int id in maybeId)
        {
            return await dbContext.Connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE MediaItem SET State = 3 WHERE Id = @Id AND State != 3",
                    parameters: new { Id = id },
                    cancellationToken: cancellationToken)).Map(count => count > 0 ? Some(id) : None);
        }

        return None;
    }

    public async Task<List<PlexItemEtag>> GetExistingShows(PlexLibrary library, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.QueryAsync<PlexItemEtag>(
                new CommandDefinition(
                    @"SELECT PS.Key, PS.Etag, MI.State FROM PlexShow PS
                      INNER JOIN MediaItem MI on PS.Id = MI.Id
                      INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId",
                    parameters: new { LibraryId = library.Id },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());
    }

    public async Task<List<PlexItemEtag>> GetExistingSeasons(
        PlexLibrary library,
        PlexShow show,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.QueryAsync<PlexItemEtag>(
                new CommandDefinition(
                    @"SELECT PlexSeason.Key, PlexSeason.Etag, MI.State FROM PlexSeason
                      INNER JOIN Season S on PlexSeason.Id = S.Id
                      INNER JOIN MediaItem MI on S.Id = MI.Id
                      INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                      INNER JOIN PlexShow PS ON S.ShowId = PS.Id
                      WHERE LP.LibraryId = @LibraryId AND PS.Key = @Key",
                    parameters: new { LibraryId = library.Id, show.Key },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());
    }

    public async Task<List<PlexItemEtag>> GetExistingEpisodes(
        PlexLibrary library,
        PlexSeason season,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.QueryAsync<PlexItemEtag>(
                new CommandDefinition(
                    @"SELECT PlexEpisode.Key, PlexEpisode.Etag, MI.State FROM PlexEpisode
                      INNER JOIN Episode E on PlexEpisode.Id = E.Id
                      INNER JOIN MediaItem MI on E.Id = MI.Id
                      INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id
                      INNER JOIN Season S2 on E.SeasonId = S2.Id
                      INNER JOIN PlexSeason PS on S2.Id = PS.Id
                      WHERE LP.LibraryId = @LibraryId AND PS.Key = @Key",
                    parameters: new { LibraryId = library.Id, season.Key },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());
    }

    public async Task<Either<BaseError, MediaItemScanResult<PlexShow>>> GetOrAdd(
        PlexLibrary library,
        PlexShow item,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<PlexShow> maybeExisting = await dbContext.PlexShows
            .AsNoTracking()
            .Where(ps => ps.LibraryPath.LibraryId == library.Id)
            .Include(i => i.ShowMetadata)
            .ThenInclude(sm => sm.Genres)
            .Include(i => i.ShowMetadata)
            .ThenInclude(sm => sm.Tags)
            .Include(i => i.ShowMetadata)
            .ThenInclude(sm => sm.Studios)
            .Include(i => i.ShowMetadata)
            .ThenInclude(sm => sm.Actors)
            .ThenInclude(a => a.Artwork)
            .Include(i => i.ShowMetadata)
            .ThenInclude(sm => sm.Artwork)
            .Include(i => i.ShowMetadata)
            .ThenInclude(sm => sm.Guids)
            .Include(i => i.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(i => i.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .SelectOneAsync(i => i.Key, i => i.Key == item.Key, cancellationToken);

        foreach (PlexShow plexShow in maybeExisting)
        {
            return new MediaItemScanResult<PlexShow>(plexShow) { IsAdded = false };
        }

        return await AddShow(dbContext, library, item, cancellationToken);
    }

    public async Task<Either<BaseError, MediaItemScanResult<PlexSeason>>> GetOrAdd(
        PlexLibrary library,
        PlexSeason item,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<PlexSeason> maybeExisting = await dbContext.PlexSeasons
            .AsNoTracking()
            .Where(ps => ps.LibraryPath.LibraryId == library.Id)
            .Include(i => i.SeasonMetadata)
            .ThenInclude(sm => sm.Artwork)
            .Include(i => i.SeasonMetadata)
            .ThenInclude(sm => sm.Guids)
            .Include(i => i.SeasonMetadata)
            .ThenInclude(sm => sm.Tags)
            .Include(s => s.LibraryPath)
            .ThenInclude(l => l.Library)
            .Include(s => s.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .SelectOneAsync(i => i.Key, i => i.Key == item.Key, cancellationToken);

        foreach (PlexSeason plexSeason in maybeExisting)
        {
            return new MediaItemScanResult<PlexSeason>(plexSeason) { IsAdded = false };
        }

        return await AddSeason(dbContext, library, item, cancellationToken);
    }

    public async Task<Either<BaseError, MediaItemScanResult<PlexEpisode>>> GetOrAdd(
        PlexLibrary library,
        PlexEpisode item,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<PlexEpisode> maybeExisting = await dbContext.PlexEpisodes
            .AsNoTracking()
            .Where(pe => pe.LibraryPath.LibraryId == library.Id)
            .Include(i => i.EpisodeMetadata)
            .ThenInclude(mm => mm.Artwork)
            .Include(i => i.EpisodeMetadata)
            .ThenInclude(mm => mm.Genres)
            .Include(i => i.EpisodeMetadata)
            .ThenInclude(mm => mm.Tags)
            .Include(i => i.EpisodeMetadata)
            .ThenInclude(mm => mm.Studios)
            .Include(i => i.EpisodeMetadata)
            .ThenInclude(mm => mm.Directors)
            .Include(i => i.EpisodeMetadata)
            .ThenInclude(mm => mm.Writers)
            .Include(i => i.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .Include(i => i.MediaVersions)
            .ThenInclude(mv => mv.Streams)
            .Include(e => e.EpisodeMetadata)
            .ThenInclude(em => em.Actors)
            .ThenInclude(a => a.Artwork)
            .Include(e => e.EpisodeMetadata)
            .ThenInclude(em => em.Guids)
            .Include(i => i.LibraryPath)
            .ThenInclude(lp => lp.Library)
            .Include(e => e.Season)
            .Include(e => e.TraktListItems)
            .ThenInclude(tli => tli.TraktList)
            .SelectOneAsync(i => i.Key, i => i.Key == item.Key, cancellationToken);

        foreach (PlexEpisode plexEpisode in maybeExisting)
        {
            var result = new MediaItemScanResult<PlexEpisode>(plexEpisode) { IsAdded = false };

            // season id can change without etag changing
            if (item.SeasonId != 0 && plexEpisode.SeasonId != item.SeasonId)
            {
                await dbContext.PlexEpisodes
                    .Where(pe => pe.Id == plexEpisode.Id)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(ee => ee.SeasonId, item.SeasonId),
                        cancellationToken);

                result.Item.SeasonId = item.SeasonId;
                result.IsUpdated = true;
            }

            if (plexEpisode.Etag != item.Etag || deepScan)
            {
                foreach (BaseError error in await UpdateEpisode(dbContext, plexEpisode, item, cancellationToken))
                {
                    return error;
                }

                result.IsUpdated = true;
            }

            return result;
        }

        return await AddEpisode(dbContext, library, item, cancellationToken);
    }

    public async Task<Unit> SetEtag(PlexShow show, string etag, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE PlexShow SET Etag = @Etag WHERE Id = @Id",
                parameters: new { Etag = etag, show.Id },
                cancellationToken: cancellationToken)).Map(_ => Unit.Default);
    }

    public async Task<Unit> SetEtag(PlexSeason season, string etag, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE PlexSeason SET Etag = @Etag WHERE Id = @Id",
                parameters: new { Etag = etag, season.Id },
                cancellationToken: cancellationToken)).Map(_ => Unit.Default);
    }

    public async Task<Unit> SetEtag(PlexEpisode episode, string etag, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE PlexEpisode SET Etag = @Etag WHERE Id = @Id",
                parameters: new { Etag = etag, episode.Id },
                cancellationToken: cancellationToken)).Map(_ => Unit.Default);
    }

    public async Task<List<int>> FlagFileNotFoundShows(
        PlexLibrary library,
        List<string> showItemIds,
        CancellationToken cancellationToken)
    {
        if (showItemIds.Count == 0)
        {
            return [];
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<int> ids = await dbContext.Connection.QueryAsync<int>(
                new CommandDefinition(
                    @"SELECT M.Id
                FROM MediaItem M
                INNER JOIN PlexShow ON PlexShow.Id = M.Id
                INNER JOIN LibraryPath LP on M.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                WHERE PlexShow.Key IN @ShowKeys",
                    parameters: new { LibraryId = library.Id, ShowKeys = showItemIds },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                @"UPDATE MediaItem SET State = 1 WHERE Id IN @Ids",
                parameters: new { Ids = ids },
                cancellationToken: cancellationToken));

        return ids;
    }

    public async Task<List<int>> FlagFileNotFoundSeasons(
        PlexLibrary library,
        List<string> seasonItemIds,
        CancellationToken cancellationToken)
    {
        if (seasonItemIds.Count == 0)
        {
            return [];
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<int> ids = await dbContext.Connection.QueryAsync<int>(
                new CommandDefinition(
                    @"SELECT M.Id
                FROM MediaItem M
                INNER JOIN PlexSeason ON PlexSeason.Id = M.Id
                INNER JOIN LibraryPath LP on M.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                WHERE PlexSeason.Key IN @SeasonKeys",
                    parameters: new { LibraryId = library.Id, SeasonKeys = seasonItemIds },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                @"UPDATE MediaItem SET State = 1 WHERE Id IN @Ids",
                parameters: new { Ids = ids },
                cancellationToken: cancellationToken));

        return ids;
    }

    public async Task<List<int>> FlagFileNotFoundEpisodes(
        PlexLibrary library,
        List<string> episodeItemIds,
        CancellationToken cancellationToken)
    {
        if (episodeItemIds.Count == 0)
        {
            return [];
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<int> ids = await dbContext.Connection.QueryAsync<int>(
                new CommandDefinition(
                    @"SELECT M.Id
                    FROM MediaItem M
                    INNER JOIN PlexEpisode ON PlexEpisode.Id = M.Id
                    INNER JOIN LibraryPath LP on M.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                    WHERE PlexEpisode.Key IN @EpisodeKeys",
                    parameters: new { LibraryId = library.Id, EpisodeKeys = episodeItemIds },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                @"UPDATE MediaItem SET State = 1 WHERE Id IN @Ids",
                parameters: new { Ids = ids },
                cancellationToken: cancellationToken));

        return ids;
    }

    public async Task<List<int>> RemoveAllTags(
        PlexLibrary library,
        PlexTag tag,
        System.Collections.Generic.HashSet<int> keep,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var tagType = tag.TagType.ToString(CultureInfo.InvariantCulture);

        List<int> result = await dbContext.ShowMetadata
            .Where(sm => !keep.Contains(sm.ShowId))
            .Where(sm => sm.Show.LibraryPath.LibraryId == library.Id)
            .Where(sm => sm.Tags.Any(t => t.Name == tag.Tag && t.ExternalTypeId == tagType))
            .Select(sm => sm.ShowId)
            .ToListAsync(cancellationToken);

        if (result.Count > 0)
        {
            List<int> tagIds = await dbContext.ShowMetadata
                .Where(sm => result.Contains(sm.ShowId))
                .SelectMany(sm => sm.Tags
                    .Where(t => t.Name == tag.Tag && t.ExternalTypeId == tagType)
                    .Select(t => t.Id))
                .ToListAsync(cancellationToken);

            // delete all tags
            await dbContext.Connection.ExecuteAsync(
                new CommandDefinition(
                    "DELETE FROM Tag WHERE Id IN @TagIds",
                    parameters: new { TagIds = tagIds },
                    cancellationToken: cancellationToken));
        }

        // show ids to refresh
        return result;
    }

    public async Task<PlexShowAddTagResult> AddTag(
        PlexLibrary library,
        PlexShow show,
        PlexTag tag,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        int existingShowId = await dbContext.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT PS.Id FROM Tag
                INNER JOIN ShowMetadata SM on SM.Id = Tag.ShowMetadataId
                INNER JOIN PlexShow PS on PS.Id = SM.ShowId
                INNER JOIN MediaItem MI on PS.Id = MI.Id
                INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                WHERE PS.Key = @Key AND Tag.Name = @Tag AND Tag.ExternalTypeId = @TagType",
                parameters: new { show.Key, tag.Tag, tag.TagType, LibraryId = library.Id },
                cancellationToken: cancellationToken));

        // already exists
        if (existingShowId > 0)
        {
            return new PlexShowAddTagResult(existingShowId, Option<int>.None);
        }

        int showId = await dbContext.PlexShows
            .Where(s => s.LibraryPath.LibraryId == library.Id)
            .Where(s => s.Key == show.Key)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (showId <= 0)
        {
            return new PlexShowAddTagResult(Option<int>.None, Option<int>.None);
        }

        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                @"INSERT INTO Tag (Name, ExternalTypeId, ShowMetadataId)
                  SELECT @Tag, @TagType, Id FROM
                  (SELECT Id FROM ShowMetadata WHERE ShowId = @ShowId) AS A",
                parameters: new { tag.Tag, tag.TagType, ShowId = showId },
                cancellationToken: cancellationToken));

        // show id to refresh
        return new PlexShowAddTagResult(Option<int>.None, showId);
    }

    public async Task UpdateLastNetworksScan(PlexLibrary library, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE PlexLibrary SET LastNetworksScan = @LastNetworksScan WHERE Id = @Id",
                parameters: new { library.LastNetworksScan, library.Id },
                cancellationToken: cancellationToken));
    }

    public async Task<Option<PlexShowTitleKeyResult>> GetShowTitleKey(
        int libraryId,
        int showId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<PlexShow> maybeShow = await dbContext.PlexShows
            .Where(s => s.Id == showId)
            .Where(s => s.LibraryPath.LibraryId == libraryId)
            .Include(s => s.ShowMetadata)
            .FirstOrDefaultAsync(cancellationToken)
            .Map(Optional);

        foreach (PlexShow show in maybeShow)
        {
            return new PlexShowTitleKeyResult(
                await show.ShowMetadata.HeadOrNone().Map(sm => sm.Title).IfNoneAsync("Unknown Show"),
                show.Key);
        }

        return Option<PlexShowTitleKeyResult>.None;
    }

    private static async Task<Either<BaseError, MediaItemScanResult<PlexShow>>> AddShow(
        TvContext dbContext,
        PlexLibrary library,
        PlexShow item,
        CancellationToken cancellationToken)
    {
        try
        {
            // blank out etag for initial save in case stats/metadata/etc updates fail
            string etag = item.Etag;
            item.Etag = string.Empty;

            item.LibraryPathId = library.Paths.Head().Id;

            await dbContext.PlexShows.AddAsync(item, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // restore etag
            item.Etag = etag;

            await dbContext.Entry(item).Reference(i => i.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(item.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<PlexShow>(item) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }

    private static async Task<Either<BaseError, MediaItemScanResult<PlexSeason>>> AddSeason(
        TvContext dbContext,
        PlexLibrary library,
        PlexSeason item,
        CancellationToken cancellationToken)
    {
        try
        {
            // blank out etag for initial save in case stats/metadata/etc updates fail
            string etag = item.Etag;
            item.Etag = string.Empty;

            item.LibraryPathId = library.Paths.Head().Id;

            await dbContext.PlexSeasons.AddAsync(item, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // restore etag
            item.Etag = etag;

            await dbContext.Entry(item).Reference(i => i.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(item.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<PlexSeason>(item) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<PlexEpisode>>> AddEpisode(
        TvContext dbContext,
        PlexLibrary library,
        PlexEpisode item,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await MediaItemRepository.MediaFileAlreadyExists(
                    item,
                    library.Paths.Head().Id,
                    dbContext,
                    _logger,
                    cancellationToken))
            {
                return new MediaFileAlreadyExists();
            }

            // blank out etag for initial save in case stats/metadata/etc updates fail
            string etag = item.Etag;
            item.Etag = string.Empty;

            item.LibraryPathId = library.Paths.Head().Id;
            foreach (EpisodeMetadata metadata in item.EpisodeMetadata)
            {
                metadata.Genres ??= [];
                metadata.Tags ??= [];
                metadata.Studios ??= [];
                metadata.Actors ??= [];
                metadata.Directors ??= [];
                metadata.Writers ??= [];
            }

            await dbContext.PlexEpisodes.AddAsync(item, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // restore etag
            item.Etag = etag;

            await dbContext.Entry(item).Reference(i => i.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(item.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            await dbContext.Entry(item).Reference(e => e.Season).LoadAsync(cancellationToken);
            return new MediaItemScanResult<PlexEpisode>(item) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.Message);
        }
    }

    private async Task<Option<BaseError>> UpdateEpisode(
        TvContext dbContext,
        PlexEpisode existing,
        PlexEpisode incoming,
        CancellationToken cancellationToken)
    {
        try
        {
            // library path is used for search indexing later
            incoming.LibraryPath = existing.LibraryPath;
            incoming.Id = existing.Id;

            // version
            MediaVersion version = existing.MediaVersions.Head();
            MediaVersion incomingVersion = incoming.MediaVersions.Head();
            version.Name = incomingVersion.Name;
            version.DateAdded = incomingVersion.DateAdded;

            // media file
            if (version.MediaFiles.Head() is PlexMediaFile file &&
                incomingVersion.MediaFiles.Head() is PlexMediaFile incomingFile)
            {
                _logger.LogDebug(
                    "Updating plex episode (key {Key}) file key from {FK1} => {FK2}, path from {Existing} to {Incoming}",
                    existing.Key,
                    file.Key,
                    incomingFile.Key,
                    file.Path,
                    incomingFile.Path);

                file.Path = incomingFile.Path;
                file.Key = incomingFile.Key;

                await dbContext.Connection.ExecuteAsync(
                    new CommandDefinition(
                        @"UPDATE MediaVersion SET Name = @Name, DateAdded = @DateAdded WHERE Id = @Id",
                        parameters: new { version.Name, version.DateAdded, version.Id },
                        cancellationToken: cancellationToken));

                await dbContext.Connection.ExecuteAsync(
                    new CommandDefinition(
                        @"UPDATE MediaFile SET Path = @Path WHERE Id = @Id",
                        parameters: new { file.Path, file.Id },
                        cancellationToken: cancellationToken));

                await dbContext.Connection.ExecuteAsync(
                    new CommandDefinition(
                        @"UPDATE PlexMediaFile SET `Key` = @Key WHERE Id = @Id",
                        parameters: new { file.Key, file.Id },
                        cancellationToken: cancellationToken));
            }

            // metadata
            foreach (EpisodeMetadata metadata in existing.EpisodeMetadata.HeadOrNone())
            {
                foreach (EpisodeMetadata incomingMetadata in incoming.EpisodeMetadata.HeadOrNone())
                {
                    if (metadata.Title != incomingMetadata.Title ||
                        metadata.Plot != incomingMetadata.Plot ||
                        metadata.Year != incomingMetadata.Year ||
                        metadata.DateAdded != incomingMetadata.DateAdded ||
                        metadata.ReleaseDate != incomingMetadata.ReleaseDate ||
                        metadata.EpisodeNumber != incomingMetadata.EpisodeNumber)
                    {
                        await dbContext.EpisodeMetadata
                            .Where(em => em.Id == metadata.Id)
                            .ExecuteUpdateAsync(
                                setters => setters
                                    .SetProperty(em => em.Title, incomingMetadata.Title)
                                    .SetProperty(em => em.SortTitle, incomingMetadata.SortTitle)
                                    .SetProperty(em => em.Plot, incomingMetadata.Plot)
                                    .SetProperty(em => em.Year, incomingMetadata.Year)
                                    .SetProperty(em => em.DateAdded, incomingMetadata.DateAdded)
                                    .SetProperty(em => em.ReleaseDate, incomingMetadata.ReleaseDate)
                                    .SetProperty(em => em.EpisodeNumber, incomingMetadata.EpisodeNumber),
                                cancellationToken);
                    }
                }
            }

            return Option<BaseError>.None;
        }
        catch (Exception)
        {
            return BaseError.New("Failed to update episode path");
        }
    }
}

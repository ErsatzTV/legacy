using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Core.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class JellyfinTelevisionRepository : IJellyfinTelevisionRepository
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ILogger<JellyfinTelevisionRepository> _logger;

    public JellyfinTelevisionRepository(
        IDbContextFactory<TvContext> dbContextFactory,
        ILogger<JellyfinTelevisionRepository> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<List<JellyfinItemEtag>> GetExistingShows(
        JellyfinLibrary library,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.QueryAsync<JellyfinItemEtag>(
                new CommandDefinition(
                    @"SELECT ItemId, Etag, MI.State FROM JellyfinShow
                      INNER JOIN `Show` S on JellyfinShow.Id = S.Id
                      INNER JOIN MediaItem MI on S.Id = MI.Id
                      INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id
                      WHERE LP.LibraryId = @LibraryId",
                    parameters: new { LibraryId = library.Id },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());
    }

    public async Task<List<JellyfinItemEtag>> GetExistingSeasons(
        JellyfinLibrary library,
        JellyfinShow show,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.QueryAsync<JellyfinItemEtag>(
                new CommandDefinition(
                    @"SELECT JellyfinSeason.ItemId, JellyfinSeason.Etag, MI.State FROM JellyfinSeason
                      INNER JOIN Season S on JellyfinSeason.Id = S.Id
                      INNER JOIN MediaItem MI on S.Id = MI.Id
                      INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id
                      INNER JOIN `Show` S2 on S.ShowId = S2.Id
                      INNER JOIN JellyfinShow JS on S2.Id = JS.Id
                      WHERE LP.LibraryId = @LibraryId AND JS.ItemId = @ShowItemId",
                    parameters: new { LibraryId = library.Id, ShowItemId = show.ItemId },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());
    }

    public async Task<List<JellyfinItemEtag>> GetExistingEpisodes(
        JellyfinLibrary library,
        JellyfinSeason season,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.QueryAsync<JellyfinItemEtag>(
                new CommandDefinition(
                    @"SELECT JellyfinEpisode.ItemId, JellyfinEpisode.Etag, MI.State FROM JellyfinEpisode
                      INNER JOIN Episode E on JellyfinEpisode.Id = E.Id
                      INNER JOIN MediaItem MI on E.Id = MI.Id
                      INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id
                      INNER JOIN Season S2 on E.SeasonId = S2.Id
                      INNER JOIN JellyfinSeason JS on S2.Id = JS.Id
                      WHERE LP.LibraryId = @LibraryId AND JS.ItemId = @SeasonItemId",
                    parameters: new { LibraryId = library.Id, SeasonItemId = season.ItemId },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());
    }

    public async Task<Either<BaseError, MediaItemScanResult<JellyfinShow>>> GetOrAdd(
        JellyfinLibrary library,
        JellyfinShow item,
        CancellationToken cancellationToken)
    {
        using (ScanProfiler.Measure("DB Ins/Upd Show"))
        {
            await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            Option<JellyfinShow> maybeExisting = await dbContext.JellyfinShows
                .TagWithCallSite()
                .Include(m => m.ShowMetadata)
                .ThenInclude(mm => mm.Genres)
                .Include(m => m.ShowMetadata)
                .ThenInclude(mm => mm.Tags)
                .Include(m => m.ShowMetadata)
                .ThenInclude(mm => mm.Studios)
                .Include(m => m.ShowMetadata)
                .ThenInclude(mm => mm.Actors)
                .Include(m => m.ShowMetadata)
                .ThenInclude(mm => mm.Artwork)
                .Include(m => m.ShowMetadata)
                .ThenInclude(mm => mm.Guids)
                .SingleOrDefaultAsync(s => s.ItemId == item.ItemId, cancellationToken);

            foreach (JellyfinShow jellyfinShow in maybeExisting)
            {
                var result = new MediaItemScanResult<JellyfinShow>(jellyfinShow) { IsAdded = false };
                if (jellyfinShow.Etag != item.Etag)
                {
                    await UpdateShow(dbContext, jellyfinShow, item, cancellationToken);
                    result.IsUpdated = true;
                }

                return result;
            }

            return await AddShow(dbContext, library, item, cancellationToken);
        }
    }

    public async Task<Either<BaseError, MediaItemScanResult<JellyfinSeason>>> GetOrAdd(
        JellyfinLibrary library,
        JellyfinSeason item,
        CancellationToken cancellationToken)
    {
        using (ScanProfiler.Measure("DB Ins/Upd Season"))
        {
            await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            Option<JellyfinSeason> maybeExisting = await dbContext.JellyfinSeasons
                .TagWithCallSite()
                .Include(m => m.SeasonMetadata)
                .ThenInclude(mm => mm.Artwork)
                .Include(m => m.SeasonMetadata)
                .ThenInclude(mm => mm.Guids)
                .SingleOrDefaultAsync(s => s.ItemId == item.ItemId, cancellationToken);

            foreach (JellyfinSeason jellyfinSeason in maybeExisting)
            {
                var result = new MediaItemScanResult<JellyfinSeason>(jellyfinSeason) { IsAdded = false };
                if (jellyfinSeason.Etag != item.Etag)
                {
                    await UpdateSeason(dbContext, jellyfinSeason, item, cancellationToken);
                    result.IsUpdated = true;
                }

                return result;
            }

            return await AddSeason(dbContext, library, item, cancellationToken);
        }
    }

    public async Task<Either<BaseError, MediaItemScanResult<JellyfinEpisode>>> GetOrAdd(
        JellyfinLibrary library,
        JellyfinEpisode item,
        bool deepScan,
        CancellationToken cancellationToken)
    {
        using (ScanProfiler.Measure("DB Ins/Upd Episode"))
        {
            await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            Option<dynamic> maybeExistingState = await dbContext.JellyfinEpisodes
                .TagWithCallSite()
                .Where(s => s.ItemId == item.ItemId)
                .Select(s => new { s.Id, s.SeasonId, s.Etag })
                .SingleOrDefaultAsync(cancellationToken);

            foreach (dynamic existingState in maybeExistingState)
            {
                int existingId = existingState.Id;

                var isUpdated = false;

                // season id can change without etag changing
                if (item.SeasonId != 0 && existingState.SeasonId != item.SeasonId)
                {
                    await dbContext.JellyfinEpisodes
                        .Where(je => je.Id == existingId)
                        .ExecuteUpdateAsync(
                            setters => setters.SetProperty(ee => ee.SeasonId, item.SeasonId),
                            cancellationToken);

                    isUpdated = true;
                }

                MediaItemScanResult<JellyfinEpisode> result;
                if (existingState.Etag != item.Etag || deepScan)
                {
                    JellyfinEpisode existing;

                    using (ScanProfiler.Measure("DB Ep Upd Load"))
                    {
                        existing = await dbContext.JellyfinEpisodes
                            .TagWithCallSite()
                            .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Artwork)
                            .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Guids)
                            .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Genres)
                            .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Tags)
                            .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Actors).ThenInclude(a => a.Artwork)
                            .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Directors)
                            .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Writers)
                            .Include(e => e.MediaVersions).ThenInclude(mv => mv.Streams)
                            .AsSplitQuery()
                            .SingleAsync(s => s.Id == existingId, cancellationToken);

                        await dbContext.Entry(existing).Reference(e => e.Season).LoadAsync(cancellationToken);
                    }

                    await UpdateEpisode(dbContext, existing, item, cancellationToken);

                    result = new MediaItemScanResult<JellyfinEpisode>(existing)
                        { IsAdded = false, IsUpdated = true };
                }
                else
                {
                    JellyfinEpisode existing = await dbContext.JellyfinEpisodes
                        .AsNoTracking()
                        .TagWithCallSite()
                        .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Actors)
                        .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Directors)
                        .Include(e => e.EpisodeMetadata).ThenInclude(em => em.Writers)
                        .Include(e => e.MediaVersions).ThenInclude(mv => mv.Streams)
                        .AsSplitQuery()
                        .SingleAsync(s => s.Id == existingId, cancellationToken);

                    result = new MediaItemScanResult<JellyfinEpisode>(existing)
                        { IsAdded = false, IsUpdated = isUpdated };
                }

                return result;
            }

            return await AddEpisode(dbContext, library, item, cancellationToken);
        }
    }

    public async Task<Unit> SetEtag(JellyfinShow show, string etag, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE JellyfinShow SET Etag = @Etag WHERE Id = @Id",
                parameters: new { Etag = etag, show.Id },
                cancellationToken: cancellationToken)).Map(_ => Unit.Default);
    }

    public async Task<Unit> SetEtag(JellyfinSeason season, string etag, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE JellyfinSeason SET Etag = @Etag WHERE Id = @Id",
                parameters: new { Etag = etag, season.Id },
                cancellationToken: cancellationToken)).Map(_ => Unit.Default);
    }

    public async Task<Unit> SetEtag(JellyfinEpisode episode, string etag, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE JellyfinEpisode SET Etag = @Etag WHERE Id = @Id",
                parameters: new { Etag = etag, episode.Id },
                cancellationToken: cancellationToken)).Map(_ => Unit.Default);
    }

    public async Task<Option<int>> FlagNormal(
        JellyfinLibrary library,
        JellyfinEpisode episode,
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
                @"SELECT JellyfinEpisode.Id FROM JellyfinEpisode
                    INNER JOIN MediaItem MI ON MI.Id = JellyfinEpisode.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE JellyfinEpisode.ItemId = @ItemId",
                parameters: new { LibraryId = library.Id, episode.ItemId },
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
        JellyfinLibrary library,
        JellyfinSeason season,
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
                @"SELECT JellyfinSeason.Id FROM JellyfinSeason
                    INNER JOIN MediaItem MI ON MI.Id = JellyfinSeason.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE JellyfinSeason.ItemId = @ItemId",
                parameters: new { LibraryId = library.Id, season.ItemId },
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
        JellyfinLibrary library,
        JellyfinShow show,
        CancellationToken cancellationToken)
    {
        if (show.State is MediaItemState.Normal)
        {
            return Option<int>.None;
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        show.State = MediaItemState.Normal;

        Option<int> maybeId = await dbContext.Connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                @"SELECT JellyfinShow.Id FROM JellyfinShow
                    INNER JOIN MediaItem MI ON MI.Id = JellyfinShow.Id
                    INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                    WHERE JellyfinShow.ItemId = @ItemId",
                parameters: new { LibraryId = library.Id, show.ItemId },
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

    public async Task<List<int>> FlagFileNotFoundShows(
        JellyfinLibrary library,
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
                        INNER JOIN JellyfinShow ON JellyfinShow.Id = M.Id
                        INNER JOIN LibraryPath LP on M.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                        WHERE JellyfinShow.ItemId IN @ShowItemIds",
                    parameters: new { LibraryId = library.Id, ShowItemIds = showItemIds },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE MediaItem SET State = 1 WHERE Id IN @Ids AND State != 1",
                parameters: new { Ids = ids },
                cancellationToken: cancellationToken));

        return ids;
    }

    public async Task<List<int>> FlagFileNotFoundSeasons(
        JellyfinLibrary library,
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
                INNER JOIN JellyfinSeason ON JellyfinSeason.Id = M.Id
                INNER JOIN LibraryPath LP on M.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                WHERE JellyfinSeason.ItemId IN @SeasonItemIds",
                    parameters: new { LibraryId = library.Id, SeasonItemIds = seasonItemIds },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE MediaItem SET State = 1 WHERE Id IN @Ids AND State != 1",
                parameters: new { Ids = ids },
                cancellationToken: cancellationToken));

        return ids;
    }

    public async Task<List<int>> FlagFileNotFoundEpisodes(
        JellyfinLibrary library,
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
                        INNER JOIN JellyfinEpisode ON JellyfinEpisode.Id = M.Id
                        INNER JOIN LibraryPath LP on M.LibraryPathId = LP.Id AND LP.LibraryId = @LibraryId
                        WHERE JellyfinEpisode.ItemId IN @EpisodeItemIds",
                    parameters: new { LibraryId = library.Id, EpisodeItemIds = episodeItemIds },
                    cancellationToken: cancellationToken))
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE MediaItem SET State = 1 WHERE Id IN @Ids AND State != 1",
                parameters: new { Ids = ids },
                cancellationToken: cancellationToken));

        return ids;
    }

    public async Task<Option<int>> FlagUnavailable(
        JellyfinLibrary library,
        JellyfinEpisode episode,
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
                @"SELECT JellyfinEpisode.Id FROM JellyfinEpisode
                  INNER JOIN MediaItem MI ON MI.Id = JellyfinEpisode.Id
                  INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                  WHERE JellyfinEpisode.ItemId = @ItemId",
                parameters: new { LibraryId = library.Id, episode.ItemId },
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
        JellyfinLibrary library,
        JellyfinEpisode episode,
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
                @"SELECT JellyfinEpisode.Id FROM JellyfinEpisode
                  INNER JOIN MediaItem MI ON MI.Id = JellyfinEpisode.Id
                  INNER JOIN LibraryPath LP on MI.LibraryPathId = LP.Id AND LibraryId = @LibraryId
                  WHERE JellyfinEpisode.ItemId = @ItemId",
                parameters: new { LibraryId = library.Id, episode.ItemId },
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

    public async Task<Option<JellyfinShowTitleItemIdResult>> GetShowTitleItemId(
        int libraryId,
        int showId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<JellyfinShow> maybeShow = await dbContext.JellyfinShows
            .AsNoTracking()
            .TagWithCallSite()
            .Where(s => s.Id == showId)
            .Where(s => s.LibraryPath.LibraryId == libraryId)
            .Include(s => s.ShowMetadata)
            .FirstOrDefaultAsync(cancellationToken)
            .Map(Optional);

        foreach (JellyfinShow show in maybeShow)
        {
            return new JellyfinShowTitleItemIdResult(
                await show.ShowMetadata.HeadOrNone().Map(sm => sm.Title).IfNoneAsync("Unknown Show"),
                show.ItemId);
        }

        return Option<JellyfinShowTitleItemIdResult>.None;
    }

    private static async Task UpdateShow(
        TvContext dbContext,
        JellyfinShow existing,
        JellyfinShow incoming,
        CancellationToken cancellationToken)
    {
        // library path is used for search indexing later
        incoming.LibraryPathId = existing.LibraryPathId;
        incoming.Id = existing.Id;

        // metadata
        ShowMetadata metadata = existing.ShowMetadata.Head();
        ShowMetadata incomingMetadata = incoming.ShowMetadata.Head();
        metadata.MetadataKind = incomingMetadata.MetadataKind;
        metadata.ContentRating = incomingMetadata.ContentRating;
        metadata.Title = incomingMetadata.Title;
        metadata.SortTitle = incomingMetadata.SortTitle;
        metadata.Plot = incomingMetadata.Plot;
        metadata.Year = incomingMetadata.Year;
        metadata.Tagline = incomingMetadata.Tagline;
        metadata.DateAdded = incomingMetadata.DateAdded;
        metadata.DateUpdated = DateTime.UtcNow;

        // genres
        foreach (Genre genre in metadata.Genres
                     .Filter(g => incomingMetadata.Genres.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Genres.Remove(genre);
        }

        foreach (Genre genre in incomingMetadata.Genres
                     .Filter(g => metadata.Genres.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Genres.Add(genre);
        }

        // tags
        foreach (Tag tag in metadata.Tags
                     .Filter(g => incomingMetadata.Tags.All(g2 => g2.Name != g.Name))
                     .Filter(g => g.ExternalCollectionId is null && g.ExternalTypeId is null)
                     .ToList())
        {
            metadata.Tags.Remove(tag);
        }

        foreach (Tag tag in incomingMetadata.Tags
                     .Filter(g => metadata.Tags.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Tags.Add(tag);
        }

        // studios
        foreach (Studio studio in metadata.Studios
                     .Filter(g => incomingMetadata.Studios.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Studios.Remove(studio);
        }

        foreach (Studio studio in incomingMetadata.Studios
                     .Filter(g => metadata.Studios.All(g2 => g2.Name != g.Name))
                     .ToList())
        {
            metadata.Studios.Add(studio);
        }

        // actors
        foreach (Actor actor in metadata.Actors
                     .Filter(a =>
                         incomingMetadata.Actors.All(a2 =>
                             a2.Name != a.Name || a.Artwork == null && a2.Artwork != null))
                     .ToList())
        {
            metadata.Actors.Remove(actor);
            dbContext.Actors.Remove(actor);
        }

        foreach (Actor actor in incomingMetadata.Actors
                     .Filter(a => metadata.Actors.All(a2 => a2.Name != a.Name))
                     .ToList())
        {
            metadata.Actors.Add(actor);
        }

        // guids
        foreach (MetadataGuid guid in metadata.Guids
                     .Filter(g => incomingMetadata.Guids.All(g2 => g2.Guid != g.Guid))
                     .ToList())
        {
            metadata.Guids.Remove(guid);
        }

        foreach (MetadataGuid guid in incomingMetadata.Guids
                     .Filter(g => metadata.Guids.All(g2 => g2.Guid != g.Guid))
                     .ToList())
        {
            metadata.Guids.Add(guid);
        }

        metadata.ReleaseDate = incomingMetadata.ReleaseDate;

        // poster
        Artwork incomingPoster =
            incomingMetadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Poster);
        if (incomingPoster != null)
        {
            Artwork poster = metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Poster);
            if (poster == null)
            {
                poster = new Artwork { ArtworkKind = ArtworkKind.Poster };
                metadata.Artwork.Add(poster);
            }

            poster.Path = incomingPoster.Path;
            poster.DateAdded = incomingPoster.DateAdded;
            poster.DateUpdated = incomingPoster.DateUpdated;
        }

        // fan art
        Artwork incomingFanArt =
            incomingMetadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.FanArt);
        if (incomingFanArt != null)
        {
            Artwork fanArt = metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.FanArt);
            if (fanArt == null)
            {
                fanArt = new Artwork { ArtworkKind = ArtworkKind.FanArt };
                metadata.Artwork.Add(fanArt);
            }

            fanArt.Path = incomingFanArt.Path;
            fanArt.DateAdded = incomingFanArt.DateAdded;
            fanArt.DateUpdated = incomingFanArt.DateUpdated;
        }

        var paths = incomingMetadata.Artwork.Map(a => a.Path).ToList();
        foreach (Artwork artworkToRemove in metadata.Artwork
                     .Filter(a => !paths.Contains(a.Path))
                     .ToList())
        {
            metadata.Artwork.Remove(artworkToRemove);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpdateSeason(
        TvContext dbContext,
        JellyfinSeason existing,
        JellyfinSeason incoming,
        CancellationToken cancellationToken)
    {
        // library path is used for search indexing later
        incoming.LibraryPathId = existing.LibraryPathId;
        incoming.Id = existing.Id;

        existing.SeasonNumber = incoming.SeasonNumber;

        // metadata
        SeasonMetadata metadata = existing.SeasonMetadata.Head();
        SeasonMetadata incomingMetadata = incoming.SeasonMetadata.Head();
        metadata.Title = incomingMetadata.Title;
        metadata.SortTitle = incomingMetadata.SortTitle;
        metadata.Year = incomingMetadata.Year;
        metadata.DateAdded = incomingMetadata.DateAdded;
        metadata.DateUpdated = DateTime.UtcNow;
        metadata.ReleaseDate = incomingMetadata.ReleaseDate;

        // poster
        Artwork incomingPoster =
            incomingMetadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Poster);
        if (incomingPoster != null)
        {
            Artwork poster = metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Poster);
            if (poster == null)
            {
                poster = new Artwork { ArtworkKind = ArtworkKind.Poster };
                metadata.Artwork.Add(poster);
            }

            poster.Path = incomingPoster.Path;
            poster.DateAdded = incomingPoster.DateAdded;
            poster.DateUpdated = incomingPoster.DateUpdated;
        }

        // thumbnail
        Artwork incomingThumbnail =
            incomingMetadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Thumbnail);
        if (incomingThumbnail != null)
        {
            Artwork thumb = metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Thumbnail);
            if (thumb == null)
            {
                thumb = new Artwork { ArtworkKind = ArtworkKind.Thumbnail };
                metadata.Artwork.Add(thumb);
            }

            thumb.Path = incomingThumbnail.Path;
            thumb.DateAdded = incomingThumbnail.DateAdded;
            thumb.DateUpdated = incomingThumbnail.DateUpdated;
        }

        // fan art
        Artwork incomingFanArt =
            incomingMetadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.FanArt);
        if (incomingFanArt != null)
        {
            Artwork fanArt = metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.FanArt);
            if (fanArt == null)
            {
                fanArt = new Artwork { ArtworkKind = ArtworkKind.FanArt };
                metadata.Artwork.Add(fanArt);
            }

            fanArt.Path = incomingFanArt.Path;
            fanArt.DateAdded = incomingFanArt.DateAdded;
            fanArt.DateUpdated = incomingFanArt.DateUpdated;
        }

        // guids
        foreach (MetadataGuid guid in metadata.Guids
                     .Filter(g => incomingMetadata.Guids.All(g2 => g2.Guid != g.Guid))
                     .ToList())
        {
            metadata.Guids.Remove(guid);
        }

        foreach (MetadataGuid guid in incomingMetadata.Guids
                     .Filter(g => metadata.Guids.All(g2 => g2.Guid != g.Guid))
                     .ToList())
        {
            metadata.Guids.Add(guid);
        }

        var paths = incomingMetadata.Artwork.Map(a => a.Path).ToList();
        foreach (Artwork artworkToRemove in metadata.Artwork
                     .Filter(a => !paths.Contains(a.Path))
                     .ToList())
        {
            metadata.Artwork.Remove(artworkToRemove);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpdateEpisode(
        TvContext dbContext,
        JellyfinEpisode existing,
        JellyfinEpisode incoming,
        CancellationToken cancellationToken)
    {
        using (ScanProfiler.Measure("DB Ep Upd Save"))
        {
            // library path is used for search indexing later
            incoming.LibraryPathId = existing.LibraryPathId;
            incoming.Id = existing.Id;

            // metadata
            // TODO: multiple metadata?
            EpisodeMetadata metadata = existing.EpisodeMetadata.Head();
            EpisodeMetadata incomingMetadata = incoming.EpisodeMetadata.Head();
            metadata.Title = incomingMetadata.Title;
            metadata.SortTitle = incomingMetadata.SortTitle;
            metadata.Plot = incomingMetadata.Plot;
            metadata.Year = incomingMetadata.Year;
            metadata.DateAdded = incomingMetadata.DateAdded;
            metadata.DateUpdated = DateTime.UtcNow;
            metadata.ReleaseDate = incomingMetadata.ReleaseDate;
            metadata.EpisodeNumber = incomingMetadata.EpisodeNumber;

            // thumbnail
            Artwork incomingThumbnail =
                incomingMetadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Thumbnail);
            if (incomingThumbnail != null)
            {
                Artwork thumbnail = metadata.Artwork.FirstOrDefault(a => a.ArtworkKind == ArtworkKind.Thumbnail);
                if (thumbnail == null)
                {
                    thumbnail = new Artwork { ArtworkKind = ArtworkKind.Thumbnail };
                    metadata.Artwork.Add(thumbnail);
                }

                thumbnail.Path = incomingThumbnail.Path;
                thumbnail.DateAdded = incomingThumbnail.DateAdded;
                thumbnail.DateUpdated = incomingThumbnail.DateUpdated;
            }

            // directors
            foreach (Director director in metadata.Directors
                         .Filter(d => incomingMetadata.Directors.All(d2 => d2.Name != d.Name))
                         .ToList())
            {
                metadata.Directors.Remove(director);
            }

            foreach (Director director in incomingMetadata.Directors
                         .Filter(d => metadata.Directors.All(d2 => d2.Name != d.Name))
                         .ToList())
            {
                metadata.Directors.Add(director);
            }

            // writers
            foreach (Writer writer in metadata.Writers
                         .Filter(w => incomingMetadata.Writers.All(w2 => w2.Name != w.Name))
                         .ToList())
            {
                metadata.Writers.Remove(writer);
            }

            foreach (Writer writer in incomingMetadata.Writers
                         .Filter(w => metadata.Writers.All(w2 => w2.Name != w.Name))
                         .ToList())
            {
                metadata.Writers.Add(writer);
            }

            // guids
            foreach (MetadataGuid guid in metadata.Guids
                         .Filter(g => incomingMetadata.Guids.All(g2 => g2.Guid != g.Guid))
                         .ToList())
            {
                metadata.Guids.Remove(guid);
            }

            foreach (MetadataGuid guid in incomingMetadata.Guids
                         .Filter(g => metadata.Guids.All(g2 => g2.Guid != g.Guid))
                         .ToList())
            {
                metadata.Guids.Add(guid);
            }

            // genres
            foreach (Genre genre in metadata.Genres
                         .Filter(g => incomingMetadata.Genres.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                metadata.Genres.Remove(genre);
            }

            foreach (Genre genre in incomingMetadata.Genres
                         .Filter(g => metadata.Genres.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                metadata.Genres.Add(genre);
            }

            // tags
            foreach (Tag tag in metadata.Tags
                         .Filter(g => incomingMetadata.Tags.All(g2 => g2.Name != g.Name))
                         .Filter(g => g.ExternalCollectionId is null && g.ExternalTypeId is null)
                         .ToList())
            {
                metadata.Tags.Remove(tag);
            }

            foreach (Tag tag in incomingMetadata.Tags
                         .Filter(g => metadata.Tags.All(g2 => g2.Name != g.Name))
                         .ToList())
            {
                metadata.Tags.Add(tag);
            }

            var paths = incomingMetadata.Artwork.Map(a => a.Path).ToList();
            foreach (Artwork artworkToRemove in metadata.Artwork
                         .Filter(a => !paths.Contains(a.Path))
                         .ToList())
            {
                metadata.Artwork.Remove(artworkToRemove);
            }

            // version
            MediaVersion version = existing.MediaVersions.Head();
            MediaVersion incomingVersion = incoming.MediaVersions.Head();
            version.Name = incomingVersion.Name;
            version.DateAdded = incomingVersion.DateAdded;

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // delete old chapters
            await dbContext.MediaChapters
                .TagWithCallSite()
                .Where(c => c.MediaVersionId == version.Id)
                .ExecuteDeleteAsync(cancellationToken);

            // replace with new chapters
            version.Chapters = incomingVersion.Chapters;
            foreach (var ch in version.Chapters)
            {
                ch.MediaVersionId = version.Id;
            }

            // always update media file path (and hash)
            MediaFile incomingFile = incomingVersion.MediaFiles.Head();
            await dbContext.MediaFiles
                .TagWithCallSite()
                .Where(mf => mf.MediaVersionId == version.Id)
                .ExecuteUpdateAsync(
                    mf => mf.SetProperty(f => f.Path, incomingFile.Path)
                        .SetProperty(f => f.PathHash, PathUtils.GetPathHash(incomingFile.Path)),
                    cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task<Either<BaseError, MediaItemScanResult<JellyfinShow>>> AddShow(
        TvContext dbContext,
        JellyfinLibrary library,
        JellyfinShow show,
        CancellationToken cancellationToken)
    {
        try
        {
            // blank out etag for initial save in case other updates fail
            string etag = show.Etag;
            show.Etag = string.Empty;

            show.LibraryPathId = library.Paths.Head().Id;

            await dbContext.AddAsync(show, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // restore etag
            show.Etag = etag;

            await dbContext.Entry(show).Reference(m => m.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(show.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<JellyfinShow>(show) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private static async Task<Either<BaseError, MediaItemScanResult<JellyfinSeason>>> AddSeason(
        TvContext dbContext,
        JellyfinLibrary library,
        JellyfinSeason season,
        CancellationToken cancellationToken)
    {
        try
        {
            // blank out etag for initial save in case other updates fail
            string etag = season.Etag;
            season.Etag = string.Empty;

            season.LibraryPathId = library.Paths.Head().Id;

            await dbContext.AddAsync(season, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // restore etag
            season.Etag = etag;

            await dbContext.Entry(season).Reference(m => m.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(season.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<JellyfinSeason>(season) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<JellyfinEpisode>>> AddEpisode(
        TvContext dbContext,
        JellyfinLibrary library,
        JellyfinEpisode episode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await MediaItemRepository.MediaFileAlreadyExists(
                    episode,
                    library.Paths.Head().Id,
                    dbContext,
                    _logger,
                    cancellationToken))
            {
                return new MediaFileAlreadyExists();
            }

            // blank out etag for initial save in case other updates fail
            string etag = episode.Etag;
            episode.Etag = string.Empty;

            episode.LibraryPathId = library.Paths.Head().Id;

            await dbContext.AddAsync(episode, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // restore etag
            episode.Etag = etag;

            await dbContext.Entry(episode).Reference(m => m.LibraryPath).LoadAsync(cancellationToken);
            await dbContext.Entry(episode.LibraryPath).Reference(lp => lp.Library).LoadAsync(cancellationToken);
            return new MediaItemScanResult<JellyfinEpisode>(episode) { IsAdded = true };
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }
}

using Dapper;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Emby;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Data.Repositories;

public class MediaSourceRepository(IDbContextFactory<TvContext> dbContextFactory, ILogger<MediaSourceRepository> logger)
    : IMediaSourceRepository
{
    public async Task<PlexMediaSource> Add(PlexMediaSource plexMediaSource)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync();
        await context.PlexMediaSources.AddAsync(plexMediaSource);
        await context.SaveChangesAsync();
        return plexMediaSource;
    }

    public async Task<List<PlexMediaSource>> GetAllPlex()
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync();
        return await context.PlexMediaSources
            .AsNoTracking()
            .Include(p => p.Connections)
            .ToListAsync();
    }

    public async Task<List<PlexPathReplacement>> GetPlexPathReplacements(int plexMediaSourceId)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync();
        return await context.PlexPathReplacements
            .Include(ppr => ppr.PlexMediaSource)
            .Filter(r => r.PlexMediaSourceId == plexMediaSourceId)
            .ToListAsync();
    }

    public async Task<Option<PlexLibrary>> GetPlexLibrary(int plexLibraryId)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync();
        return await context.PlexLibraries
            .Include(l => l.Paths)
            .OrderBy(l => l.Id) // https://github.com/dotnet/efcore/issues/22579
            .SingleOrDefaultAsync(l => l.Id == plexLibraryId)
            .Map(Optional);
    }

    public async Task<Option<PlexMediaSource>> GetPlex(int id, CancellationToken cancellationToken)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PlexMediaSources
            .AsNoTracking()
            .Include(p => p.Connections)
            .Include(p => p.Libraries)
            .Include(p => p.PathReplacements)
            .SelectOneAsync(s => s.Id, s => s.Id == id, cancellationToken);
    }

    public async Task<Option<PlexMediaSource>> GetPlexByLibraryId(int plexLibraryId)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        int? id = await dbContext.Connection.QuerySingleOrDefaultAsync<int?>(
            @"SELECT L.MediaSourceId FROM Library L
                INNER JOIN PlexLibrary PL on L.Id = PL.Id
                WHERE L.Id = @PlexLibraryId",
            new { PlexLibraryId = plexLibraryId });

        return await dbContext.PlexMediaSources
            .Include(p => p.Connections)
            .Include(p => p.Libraries)
            .OrderBy(p => p.Id)
            .SingleOrDefaultAsync(p => p.Id == id)
            .Map(Optional);
    }

    public async Task<List<PlexPathReplacement>> GetPlexPathReplacementsByLibraryId(
        int plexLibraryPathId,
        CancellationToken cancellationToken)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PlexPathReplacements
            .FromSqlRaw(
                @"select ppr.* from LibraryPath lp
                    inner join PlexLibrary pl ON pl.Id = lp.LibraryId
                    inner join Library l ON l.Id = pl.Id
                    inner join PlexPathReplacement ppr on ppr.PlexMediaSourceId = l.MediaSourceId
                    where lp.Id = {0}",
                plexLibraryPathId)
            .Include(ppr => ppr.PlexMediaSource)
            .ToListAsync(cancellationToken);
    }

    public async Task Update(
        PlexMediaSource plexMediaSource,
        List<PlexConnection> toAdd,
        List<PlexConnection> toDelete)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        dbContext.Entry(plexMediaSource).State = EntityState.Modified;

        if (toAdd.Count != 0 || toDelete.Count != 0)
        {
            plexMediaSource.Connections.Clear();
            await dbContext.Entry(plexMediaSource).Collection(pms => pms.Connections).LoadAsync();

            plexMediaSource.Connections.AddRange(toAdd);
            plexMediaSource.Connections.RemoveAll(toDelete.Contains);
        }
        else
        {
            foreach (PlexConnection connection in plexMediaSource.Connections)
            {
                dbContext.Entry(connection).State = EntityState.Modified;
            }
        }

        // TODO: possibly caused by https://github.com/dotnet/efcore/issues/33133
        var success = false;
        var attempts = 0;
        while (!success && attempts < 3)
        {
            try
            {
                await dbContext.SaveChangesAsync();
                success = true;
            }
            catch (DbUpdateException)
            {
                // do nothing
                attempts++;
            }
        }
    }

    public async Task<List<int>> UpdateLibraries(
        int plexMediaSourceId,
        List<PlexLibrary> toAdd,
        List<PlexLibrary> toDelete,
        List<PlexLibrary> toUpdate,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        foreach (PlexLibrary add in toAdd)
        {
            add.MediaSourceId = plexMediaSourceId;
            dbContext.Entry(add).State = EntityState.Added;
            foreach (LibraryPath path in add.Paths)
            {
                dbContext.Entry(path).State = EntityState.Added;
            }
        }

        var libraryIds = toDelete.Map(l => l.Id).ToList();
        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync(cancellationToken);

        foreach (PlexLibrary delete in toDelete)
        {
            dbContext.PlexLibraries.Remove(delete);
        }

        foreach (PlexLibrary incoming in toUpdate)
        {
            Option<PlexLibrary> maybeExisting = await dbContext.PlexLibraries
                .Include(l => l.Paths)
                .SingleOrDefaultAsync(
                    l => l.Key == incoming.Key && l.MediaSourceId == plexMediaSourceId,
                    cancellationToken);

            foreach (PlexLibrary existingLibrary in maybeExisting)
            {
                // update library type, but only if not synchronized
                if (incoming.MediaKind != existingLibrary.MediaKind)
                {
                    if (existingLibrary.ShouldSyncItems)
                    {
                        logger.LogWarning(
                            "Plex library \"{Name}\" should be type {NewType} (currently {OldType}) but cannot be updated while synchronization is enabled for this library.",
                            incoming.Name,
                            incoming.MediaKind,
                            existingLibrary.MediaKind);
                    }
                    else
                    {
                        existingLibrary.MediaKind = incoming.MediaKind;
                    }
                }

                // update library path (for other video metadata)
                foreach (LibraryPath existing in existingLibrary.Paths.HeadOrNone())
                {
                    foreach (LibraryPath path in incoming.Paths.HeadOrNone())
                    {
                        existing.Path = path.Path;
                    }
                }

                existingLibrary.Name = incoming.Name;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return deletedMediaIds;
    }

    public async Task<List<int>> UpdateLibraries(
        int jellyfinMediaSourceId,
        List<JellyfinLibrary> toAdd,
        List<JellyfinLibrary> toDelete,
        List<JellyfinLibrary> toUpdate,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        foreach (JellyfinLibrary add in toAdd)
        {
            add.MediaSourceId = jellyfinMediaSourceId;
            dbContext.JellyfinLibraries.Add(add);
        }

        var libraryIds = toDelete.Map(l => l.Id).ToList();
        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync(cancellationToken);

        foreach (JellyfinLibrary delete in toDelete)
        {
            dbContext.JellyfinLibraries.Remove(delete);
        }

        foreach (JellyfinLibrary incoming in toUpdate)
        {
            Option<JellyfinLibrary> maybeExisting = await dbContext.JellyfinLibraries
                .Include(l => l.PathInfos)
                .SingleOrDefaultAsync(
                    l => l.ItemId == incoming.ItemId && l.MediaSourceId == jellyfinMediaSourceId,
                    cancellationToken);

            foreach (JellyfinLibrary existing in maybeExisting)
            {
                // update library type, but only if not synchronized
                if (incoming.MediaKind != existing.MediaKind)
                {
                    if (existing.ShouldSyncItems)
                    {
                        logger.LogWarning(
                            "Jellyfin library \"{Name}\" should be type {NewType} (currently {OldType}) but cannot be updated while synchronization is enabled for this library.",
                            incoming.Name,
                            incoming.MediaKind,
                            existing.MediaKind);
                    }
                    else
                    {
                        existing.MediaKind = incoming.MediaKind;
                    }
                }

                // remove paths that are not on the incoming version
                existing.PathInfos.RemoveAll(pi => incoming.PathInfos.All(upi => upi.Path != pi.Path));

                // update all remaining paths
                foreach (JellyfinPathInfo existingPathInfo in existing.PathInfos)
                {
                    Option<JellyfinPathInfo> maybeIncoming = incoming.PathInfos
                        .Find(pi => pi.Path == existingPathInfo.Path);
                    foreach (JellyfinPathInfo incomingPathInfo in maybeIncoming)
                    {
                        existingPathInfo.NetworkPath = incomingPathInfo.NetworkPath;
                    }
                }

                foreach (JellyfinPathInfo incomingPathInfo in incoming.PathInfos
                             .Filter(pi => existing.PathInfos.All(epi => epi.Path != pi.Path)))
                {
                    existing.PathInfos.Add(incomingPathInfo);
                }

                existing.Name = incoming.Name;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return deletedMediaIds;
    }

    public async Task<List<int>> UpdateLibraries(
        int embyMediaSourceId,
        List<EmbyLibrary> toAdd,
        List<EmbyLibrary> toDelete,
        List<EmbyLibrary> toUpdate,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        foreach (EmbyLibrary add in toAdd)
        {
            add.MediaSourceId = embyMediaSourceId;
            dbContext.EmbyLibraries.Add(add);
        }

        var libraryIds = toDelete.Map(l => l.Id).ToList();
        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync(cancellationToken);

        foreach (EmbyLibrary delete in toDelete)
        {
            dbContext.EmbyLibraries.Remove(delete);
        }

        foreach (EmbyLibrary incoming in toUpdate)
        {
            Option<EmbyLibrary> maybeExisting = await dbContext.EmbyLibraries
                .Include(l => l.PathInfos)
                .SingleOrDefaultAsync(
                    l => l.ItemId == incoming.ItemId && l.MediaSourceId == embyMediaSourceId,
                    cancellationToken);

            foreach (EmbyLibrary existing in maybeExisting)
            {
                // update library type, but only if not synchronized
                if (incoming.MediaKind != existing.MediaKind)
                {
                    if (existing.ShouldSyncItems)
                    {
                        logger.LogWarning(
                            "Emby library \"{Name}\" should be type {NewType} (currently {OldType}) but cannot be updated while synchronization is enabled for this library.",
                            incoming.Name,
                            incoming.MediaKind,
                            existing.MediaKind);
                    }
                    else
                    {
                        existing.MediaKind = incoming.MediaKind;
                    }
                }

                // remove paths that are not on the incoming version
                existing.PathInfos.RemoveAll(pi => incoming.PathInfos.All(upi => upi.Path != pi.Path));

                // update all remaining paths
                foreach (EmbyPathInfo existingPathInfo in existing.PathInfos)
                {
                    Option<EmbyPathInfo> maybeIncoming = incoming.PathInfos
                        .Find(pi => pi.Path == existingPathInfo.Path);
                    foreach (EmbyPathInfo incomingPathInfo in maybeIncoming)
                    {
                        existingPathInfo.NetworkPath = incomingPathInfo.NetworkPath;
                    }
                }

                foreach (EmbyPathInfo incomingPathInfo in incoming.PathInfos
                             .Filter(pi => existing.PathInfos.All(epi => epi.Path != pi.Path)))
                {
                    existing.PathInfos.Add(incomingPathInfo);
                }

                existing.Name = incoming.Name;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return deletedMediaIds;
    }

    public async Task<Unit> UpdatePathReplacements(
        int plexMediaSourceId,
        List<PlexPathReplacement> toAdd,
        List<PlexPathReplacement> toUpdate,
        List<PlexPathReplacement> toDelete)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        foreach (PlexPathReplacement add in toAdd)
        {
            await dbContext.Connection.ExecuteAsync(
                @"INSERT INTO PlexPathReplacement
                    (PlexPath, LocalPath, PlexMediaSourceId)
                    VALUES (@PlexPath, @LocalPath, @PlexMediaSourceId)",
                new { add.PlexPath, add.LocalPath, PlexMediaSourceId = plexMediaSourceId });
        }

        foreach (PlexPathReplacement update in toUpdate)
        {
            await dbContext.Connection.ExecuteAsync(
                @"UPDATE PlexPathReplacement
                    SET PlexPath = @PlexPath, LocalPath = @LocalPath
                    WHERE Id = @Id",
                new { update.PlexPath, update.LocalPath, update.Id });
        }

        foreach (PlexPathReplacement delete in toDelete)
        {
            await dbContext.Connection.ExecuteAsync(
                @"DELETE FROM PlexPathReplacement WHERE Id = @Id",
                new { delete.Id });
        }

        return Unit.Default;
    }

    public async Task<List<int>> DeleteAllPlex()
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        List<PlexMediaSource> allMediaSources = await dbContext.PlexMediaSources.ToListAsync();
        dbContext.PlexMediaSources.RemoveRange(allMediaSources);

        List<PlexLibrary> allPlexLibraries = await dbContext.PlexLibraries.ToListAsync();
        dbContext.PlexLibraries.RemoveRange(allPlexLibraries);
        var libraryIds = allPlexLibraries.Map(l => l.Id).ToList();

        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync();

        await dbContext.SaveChangesAsync();

        return deletedMediaIds;
    }

    public async Task<List<int>> DeletePlex(PlexMediaSource plexMediaSource)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        List<int> mediaItemIds = await dbContext.Connection.QueryAsync<int>(
                @"SELECT MediaItem.Id FROM MediaItem
                  INNER JOIN LibraryPath LP on MediaItem.LibraryPathId = LP.Id
                  INNER JOIN Library L on LP.LibraryId = L.Id
                  WHERE L.MediaSourceId = @PlexMediaSourceId",
                new { PlexMediaSourceId = plexMediaSource.Id })
            .Map(result => result.ToList());

        await dbContext.Connection.ExecuteAsync(
            @"DELETE FROM MediaSource WHERE Id = @PlexMediaSourceId",
            new { PlexMediaSourceId = plexMediaSource.Id });

        return mediaItemIds;
    }

    public async Task<List<int>> DisablePlexLibrarySync(List<int> libraryIds)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync();

        List<PlexLibrary> libraries = await dbContext.PlexLibraries
            .Include(l => l.Paths)
            .Filter(l => libraryIds.Contains(l.Id))
            .ToListAsync();

        dbContext.PlexLibraries.RemoveRange(libraries);
        await dbContext.SaveChangesAsync();

        foreach (PlexLibrary library in libraries)
        {
            library.Id = 0;
            library.ShouldSyncItems = false;
            library.LastScan = SystemTime.MinValueUtc;
        }

        await dbContext.PlexLibraries.AddRangeAsync(libraries);
        await dbContext.SaveChangesAsync();

        return deletedMediaIds;
    }

    public async Task EnablePlexLibrarySync(IEnumerable<int> libraryIds)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        await dbContext.Connection.ExecuteAsync(
            "UPDATE PlexLibrary SET ShouldSyncItems = 1 WHERE Id IN @ids",
            new { ids = libraryIds });
    }

    public async Task<Unit> UpsertJellyfin(string address, string serverName, string operatingSystem)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        Option<JellyfinMediaSource> maybeExisting = dbContext.JellyfinMediaSources
            .Include(ms => ms.Connections)
            .OrderBy(ms => ms.Id)
            .HeadOrNone();

        return await maybeExisting.Match(
            async jellyfinMediaSource =>
            {
                if (jellyfinMediaSource.Connections.Count == 0)
                {
                    jellyfinMediaSource.Connections.Add(new JellyfinConnection { Address = address });
                }
                else if (jellyfinMediaSource.Connections.Head().Address != address)
                {
                    jellyfinMediaSource.Connections.Head().Address = address;
                }

                if (jellyfinMediaSource.ServerName != serverName)
                {
                    jellyfinMediaSource.ServerName = serverName;
                }

                if (jellyfinMediaSource.OperatingSystem != operatingSystem)
                {
                    jellyfinMediaSource.OperatingSystem = operatingSystem;
                }

                await dbContext.SaveChangesAsync();

                return Unit.Default;
            },
            async () =>
            {
                var mediaSource = new JellyfinMediaSource
                {
                    ServerName = serverName,
                    OperatingSystem = operatingSystem,
                    Connections = new List<JellyfinConnection>
                    {
                        new() { Address = address }
                    },
                    PathReplacements = new List<JellyfinPathReplacement>()
                };

                await dbContext.AddAsync(mediaSource);
                await dbContext.SaveChangesAsync();

                return Unit.Default;
            });
    }

    public async Task<List<JellyfinMediaSource>> GetAllJellyfin(CancellationToken cancellationToken)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.JellyfinMediaSources
            .AsNoTracking()
            .Include(p => p.Connections)
            .ToListAsync(cancellationToken);
    }

    public async Task<Option<JellyfinMediaSource>> GetJellyfin(int id)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync();
        return await context.JellyfinMediaSources
            .Include(p => p.Connections)
            .Include(p => p.Libraries)
            .ThenInclude(l => (l as JellyfinLibrary).PathInfos)
            .Include(p => p.PathReplacements)
            .OrderBy(s => s.Id) // https://github.com/dotnet/efcore/issues/22579
            .SingleOrDefaultAsync(p => p.Id == id)
            .Map(Optional);
    }

    public async Task<List<JellyfinLibrary>> GetJellyfinLibraries(int jellyfinMediaSourceId)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync();
        return await context.JellyfinLibraries
            .Filter(l => l.MediaSourceId == jellyfinMediaSourceId)
            .ToListAsync();
    }

    public async Task<Unit> EnableJellyfinLibrarySync(IEnumerable<int> libraryIds)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE JellyfinLibrary SET ShouldSyncItems = 1 WHERE Id IN @ids",
            new { ids = libraryIds }).ToUnit();
    }

    public async Task<List<int>> DisableJellyfinLibrarySync(List<int> libraryIds)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync();

        List<JellyfinLibrary> libraries = await dbContext.JellyfinLibraries
            .Include(l => l.Paths)
            .Include(l => l.PathInfos)
            .Filter(l => libraryIds.Contains(l.Id))
            .ToListAsync();

        dbContext.JellyfinLibraries.RemoveRange(libraries);
        await dbContext.SaveChangesAsync();

        foreach (JellyfinLibrary library in libraries)
        {
            library.Id = 0;
            library.ShouldSyncItems = false;
            library.LastScan = SystemTime.MinValueUtc;
        }

        await dbContext.JellyfinLibraries.AddRangeAsync(libraries);
        await dbContext.SaveChangesAsync();

        return deletedMediaIds;
    }

    public async Task<Option<JellyfinLibrary>> GetJellyfinLibrary(int jellyfinLibraryId)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync();
        return await context.JellyfinLibraries
            .Include(l => l.Paths)
            .Include(l => l.PathInfos)
            .Include(l => l.MediaSource)
            .OrderBy(l => l.Id) // https://github.com/dotnet/efcore/issues/22579
            .SingleOrDefaultAsync(l => l.Id == jellyfinLibraryId)
            .Map(Optional);
    }

    public async Task<Option<JellyfinMediaSource>> GetJellyfinByLibraryId(int jellyfinLibraryId)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        int? id = await dbContext.Connection.QuerySingleOrDefaultAsync<int?>(
            @"SELECT L.MediaSourceId FROM Library L
                INNER JOIN JellyfinLibrary PL on L.Id = PL.Id
                WHERE L.Id = @JellyfinLibraryId",
            new { JellyfinLibraryId = jellyfinLibraryId });

        return await dbContext.JellyfinMediaSources
            .Include(p => p.Connections)
            .Include(p => p.Libraries)
            .OrderBy(p => p.Id)
            .SingleOrDefaultAsync(p => p.Id == id)
            .Map(Optional);
    }

    public async Task<List<JellyfinPathReplacement>> GetJellyfinPathReplacements(int jellyfinMediaSourceId)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.JellyfinPathReplacements
            .Filter(r => r.JellyfinMediaSourceId == jellyfinMediaSourceId)
            .Include(jpr => jpr.JellyfinMediaSource)
            .ToListAsync();
    }

    public async Task<List<JellyfinPathReplacement>> GetJellyfinPathReplacementsByLibraryId(
        int jellyfinLibraryPathId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.JellyfinPathReplacements
            .FromSqlRaw(
                @"select jpr.* from LibraryPath lp
                    inner join JellyfinLibrary jl ON jl.Id = lp.LibraryId
                    inner join Library l ON l.Id = jl.Id
                    inner join JellyfinPathReplacement jpr on jpr.JellyfinMediaSourceId = l.MediaSourceId
                    where lp.Id = {0}",
                jellyfinLibraryPathId)
            .Include(jpr => jpr.JellyfinMediaSource)
            .ToListAsync(cancellationToken);
    }

    public async Task<Unit> UpdatePathReplacements(
        int jellyfinMediaSourceId,
        List<JellyfinPathReplacement> toAdd,
        List<JellyfinPathReplacement> toUpdate,
        List<JellyfinPathReplacement> toDelete)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        foreach (JellyfinPathReplacement add in toAdd)
        {
            await dbContext.Connection.ExecuteAsync(
                @"INSERT INTO JellyfinPathReplacement
                    (JellyfinPath, LocalPath, JellyfinMediaSourceId)
                    VALUES (@JellyfinPath, @LocalPath, @JellyfinMediaSourceId)",
                new { add.JellyfinPath, add.LocalPath, JellyfinMediaSourceId = jellyfinMediaSourceId });
        }

        foreach (JellyfinPathReplacement update in toUpdate)
        {
            await dbContext.Connection.ExecuteAsync(
                @"UPDATE JellyfinPathReplacement
                    SET JellyfinPath = @JellyfinPath, LocalPath = @LocalPath
                    WHERE Id = @Id",
                new { update.JellyfinPath, update.LocalPath, update.Id });
        }

        foreach (JellyfinPathReplacement delete in toDelete)
        {
            await dbContext.Connection.ExecuteAsync(
                @"DELETE FROM JellyfinPathReplacement WHERE Id = @Id",
                new { delete.Id });
        }

        return Unit.Default;
    }

    public async Task<List<int>> DeleteAllJellyfin()
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        List<JellyfinMediaSource> allMediaSources = await dbContext.JellyfinMediaSources.ToListAsync();
        var mediaSourceIds = allMediaSources.Map(ms => ms.Id).ToList();
        dbContext.JellyfinMediaSources.RemoveRange(allMediaSources);

        List<JellyfinLibrary> allJellyfinLibraries = await dbContext.JellyfinLibraries
            .Where(l => mediaSourceIds.Contains(l.MediaSourceId))
            .ToListAsync();
        var libraryIds = allJellyfinLibraries.Map(l => l.Id).ToList();
        dbContext.JellyfinLibraries.RemoveRange(allJellyfinLibraries);

        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync();

        await dbContext.SaveChangesAsync();

        return deletedMediaIds;
    }

    public async Task<Unit> UpsertEmby(string address, string serverName, string operatingSystem)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        Option<EmbyMediaSource> maybeExisting = dbContext.EmbyMediaSources
            .Include(ms => ms.Connections)
            .OrderBy(ms => ms.Id)
            .HeadOrNone();

        return await maybeExisting.Match(
            async embyMediaSource =>
            {
                if (embyMediaSource.Connections.Count == 0)
                {
                    embyMediaSource.Connections.Add(new EmbyConnection { Address = address });
                }
                else if (embyMediaSource.Connections.Head().Address != address)
                {
                    embyMediaSource.Connections.Head().Address = address;
                }

                if (embyMediaSource.ServerName != serverName)
                {
                    embyMediaSource.ServerName = serverName;
                }

                if (embyMediaSource.OperatingSystem != operatingSystem)
                {
                    embyMediaSource.OperatingSystem = operatingSystem;
                }

                await dbContext.SaveChangesAsync();

                return Unit.Default;
            },
            async () =>
            {
                var mediaSource = new EmbyMediaSource
                {
                    ServerName = serverName,
                    OperatingSystem = operatingSystem,
                    Connections = new List<EmbyConnection>
                    {
                        new() { Address = address }
                    },
                    PathReplacements = new List<EmbyPathReplacement>()
                };

                await dbContext.AddAsync(mediaSource);
                await dbContext.SaveChangesAsync();

                return Unit.Default;
            });
    }

    public async Task<List<EmbyMediaSource>> GetAllEmby(CancellationToken cancellationToken)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EmbyMediaSources
            .AsNoTracking()
            .Include(p => p.Connections)
            .ToListAsync(cancellationToken);
    }

    public async Task<Option<EmbyMediaSource>> GetEmby(int id, CancellationToken cancellationToken)
    {
        await using TvContext context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.EmbyMediaSources
            .AsNoTracking()
            .Include(p => p.Connections)
            .Include(p => p.Libraries)
            .ThenInclude(l => (l as EmbyLibrary).PathInfos)
            .Include(p => p.PathReplacements)
            .SelectOneAsync(s => s.Id, s => s.Id == id, cancellationToken);
    }

    public async Task<Option<EmbyMediaSource>> GetEmbyByLibraryId(int embyLibraryId)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        int? id = await dbContext.Connection.QuerySingleOrDefaultAsync<int?>(
            @"SELECT L.MediaSourceId FROM Library L
                INNER JOIN EmbyLibrary PL on L.Id = PL.Id
                WHERE L.Id = @EmbyLibraryId",
            new { EmbyLibraryId = embyLibraryId });

        return await dbContext.EmbyMediaSources
            .Include(p => p.Connections)
            .Include(p => p.Libraries)
            .OrderBy(p => p.Id)
            .SingleOrDefaultAsync(p => p.Id == id)
            .Map(Optional);
    }

    public async Task<Option<EmbyLibrary>> GetEmbyLibrary(int embyLibraryId, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.EmbyLibraries
            .AsNoTracking()
            .Include(l => l.Paths)
            .Include(l => l.PathInfos)
            .Include(l => l.MediaSource)
            .SelectOneAsync(l => l.Id, l => l.Id == embyLibraryId, cancellationToken);
    }

    public async Task<List<EmbyLibrary>> GetEmbyLibraries(int embyMediaSourceId)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.EmbyLibraries
            .Filter(l => l.MediaSourceId == embyMediaSourceId)
            .ToListAsync();
    }

    public async Task<List<EmbyPathReplacement>> GetEmbyPathReplacements(int embyMediaSourceId)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.EmbyPathReplacements
            .Filter(r => r.EmbyMediaSourceId == embyMediaSourceId)
            .Include(jpr => jpr.EmbyMediaSource)
            .ToListAsync();
    }

    public async Task<List<EmbyPathReplacement>> GetEmbyPathReplacementsByLibraryId(
        int embyLibraryPathId,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.EmbyPathReplacements
            .FromSqlRaw(
                @"select epr.* from LibraryPath lp
                    inner join EmbyLibrary el ON el.Id = lp.LibraryId
                    inner join Library l ON l.Id = el.Id
                    inner join EmbyPathReplacement epr on epr.EmbyMediaSourceId = l.MediaSourceId
                    where lp.Id = {0}",
                embyLibraryPathId)
            .Include(jpr => jpr.EmbyMediaSource)
            .ToListAsync(cancellationToken);
    }

    public async Task<Unit> UpdatePathReplacements(
        int embyMediaSourceId,
        List<EmbyPathReplacement> toAdd,
        List<EmbyPathReplacement> toUpdate,
        List<EmbyPathReplacement> toDelete)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        foreach (EmbyPathReplacement add in toAdd)
        {
            await dbContext.Connection.ExecuteAsync(
                @"INSERT INTO EmbyPathReplacement
                    (EmbyPath, LocalPath, EmbyMediaSourceId)
                    VALUES (@EmbyPath, @LocalPath, @EmbyMediaSourceId)",
                new { add.EmbyPath, add.LocalPath, EmbyMediaSourceId = embyMediaSourceId });
        }

        foreach (EmbyPathReplacement update in toUpdate)
        {
            await dbContext.Connection.ExecuteAsync(
                @"UPDATE EmbyPathReplacement
                    SET EmbyPath = @EmbyPath, LocalPath = @LocalPath
                    WHERE Id = @Id",
                new { update.EmbyPath, update.LocalPath, update.Id });
        }

        foreach (EmbyPathReplacement delete in toDelete)
        {
            await dbContext.Connection.ExecuteAsync(
                @"DELETE FROM EmbyPathReplacement WHERE Id = @Id",
                new { delete.Id });
        }

        return Unit.Default;
    }

    public async Task<List<int>> DeleteAllEmby()
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        List<EmbyMediaSource> allMediaSources = await dbContext.EmbyMediaSources.ToListAsync();
        var mediaSourceIds = allMediaSources.Map(ms => ms.Id).ToList();
        dbContext.EmbyMediaSources.RemoveRange(allMediaSources);

        List<EmbyLibrary> allEmbyLibraries = await dbContext.EmbyLibraries
            .Where(l => mediaSourceIds.Contains(l.MediaSourceId))
            .ToListAsync();
        var libraryIds = allEmbyLibraries.Map(l => l.Id).ToList();
        dbContext.EmbyLibraries.RemoveRange(allEmbyLibraries);

        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync();

        await dbContext.SaveChangesAsync();

        return deletedMediaIds;
    }

    public async Task<Unit> EnableEmbyLibrarySync(IEnumerable<int> libraryIds)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE EmbyLibrary SET ShouldSyncItems = 1 WHERE Id IN @ids",
            new { ids = libraryIds }).Map(_ => Unit.Default);
    }

    public async Task<List<int>> DisableEmbyLibrarySync(List<int> libraryIds)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();

        List<int> deletedMediaIds = await dbContext.MediaItems
            .Filter(mi => libraryIds.Contains(mi.LibraryPath.LibraryId))
            .Map(mi => mi.Id)
            .ToListAsync();

        List<EmbyLibrary> libraries = await dbContext.EmbyLibraries
            .Include(l => l.Paths)
            .Include(l => l.PathInfos)
            .Filter(l => libraryIds.Contains(l.Id))
            .ToListAsync();

        dbContext.EmbyLibraries.RemoveRange(libraries);
        await dbContext.SaveChangesAsync();

        foreach (EmbyLibrary library in libraries)
        {
            library.Id = 0;
            library.ShouldSyncItems = false;
            library.LastScan = SystemTime.MinValueUtc;
        }

        await dbContext.EmbyLibraries.AddRangeAsync(libraries);
        await dbContext.SaveChangesAsync();

        return deletedMediaIds;
    }

    public async Task<Unit> UpdateLastCollectionScan(EmbyMediaSource embyMediaSource)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE EmbyMediaSource SET LastCollectionsScan = @LastCollectionsScan WHERE Id = @Id",
            new { embyMediaSource.LastCollectionsScan, embyMediaSource.Id }).ToUnit();
    }

    public async Task<Unit> UpdateLastCollectionScan(JellyfinMediaSource jellyfinMediaSource)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE JellyfinMediaSource SET LastCollectionsScan = @LastCollectionsScan WHERE Id = @Id",
            new { jellyfinMediaSource.LastCollectionsScan, jellyfinMediaSource.Id }).ToUnit();
    }

    public async Task<Unit> UpdateLastCollectionScan(PlexMediaSource plexMediaSource)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE PlexMediaSource SET LastCollectionsScan = @LastCollectionsScan WHERE Id = @Id",
            new { plexMediaSource.LastCollectionsScan, plexMediaSource.Id }).ToUnit();
    }

    public async Task<Unit> SetPlexMissingSince(int plexMediaSourceId, DateTime? missingSince)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Connection.ExecuteAsync(
            "UPDATE PlexMediaSource SET MissingSince = @MissingSince WHERE Id = @Id",
            new { MissingSince = missingSince, Id = plexMediaSourceId }).ToUnit();
    }
}

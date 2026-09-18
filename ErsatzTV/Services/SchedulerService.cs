using System.Globalization;
using System.Threading.Channels;
using ErsatzTV.Application;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Emby;
using ErsatzTV.Application.FFmpeg;
using ErsatzTV.Application.Graphics;
using ErsatzTV.Application.Jellyfin;
using ErsatzTV.Application.Maintenance;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.Plex;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Services;

public class SchedulerService : BackgroundService
{
    private readonly IEntityLocker _entityLocker;
    private readonly ILogger<SchedulerService> _logger;
    private readonly ChannelWriter<IScannerBackgroundServiceRequest> _scannerWorkerChannel;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly SystemStartup _systemStartup;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public SchedulerService(
        IServiceScopeFactory serviceScopeFactory,
        ChannelWriter<IBackgroundServiceRequest> workerChannel,
        ChannelWriter<IScannerBackgroundServiceRequest> scannerWorkerChannel,
        IEntityLocker entityLocker,
        SystemStartup systemStartup,
        ILogger<SchedulerService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _workerChannel = workerChannel;
        _scannerWorkerChannel = scannerWorkerChannel;
        _entityLocker = entityLocker;
        _systemStartup = systemStartup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await _systemStartup.WaitForDatabase(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogInformation("Scheduler service started");

            DateTime firstRun = DateTime.Now;

            await SyncAllNextPlayouts(stoppingToken);

            await _systemStartup.WaitForSearchIndex(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            // run once immediately at startup
            if (!stoppingToken.IsCancellationRequested)
            {
                await QueueFFmpegCapabilitiesRefresh(stoppingToken);
                await DoWork(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                int currentMinutes = DateTime.Now.TimeOfDay.Minutes;
                int toWait = currentMinutes < 30 ? 30 - currentMinutes : 60 - currentMinutes;
                _logger.LogDebug("Scheduler sleeping for {Minutes} minutes", toWait);

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(toWait), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // do nothing
                }

                if (!stoppingToken.IsCancellationRequested)
                {
                    var roundedMinute = (int)(Math.Round(DateTime.Now.Minute / 5.0) * 5);
                    if (roundedMinute % 30 == 0)
                    {
                        // check for playouts to reset every 30 minutes
                        await ResetPlayouts(stoppingToken);
                    }

                    if (roundedMinute % 60 == 0 && DateTime.Now.Subtract(firstRun) > TimeSpan.FromHours(1))
                    {
                        // do other work every hour (on the hour)
                        await DoWork(stoppingToken);
                    }
                    else if (roundedMinute % 30 == 0)
                    {
                        // release memory every 30 minutes no matter what
                        await ReleaseMemory(stoppingToken);
                    }
                }
            }

            stoppingToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduler service shutting down");
        }
    }

    private async Task DoWork(CancellationToken cancellationToken)
    {
        try
        {
            await DeleteOrphanedSubtitles(cancellationToken);
            await DeleteOrphanedArtwork(cancellationToken);
            await RefreshMpegTsScripts(cancellationToken);
            await RefreshChannelGuideChannelList(cancellationToken);
            await BuildPlayouts(cancellationToken);
#if !DEBUG_NO_SYNC
            await ScanLocalMediaSources(cancellationToken);
            await ScanPlexMediaSources(cancellationToken);
            await ScanJellyfinMediaSources(cancellationToken);
            await ScanEmbyMediaSources(cancellationToken);
#endif
            await RefreshTraktLists(cancellationToken);
            await MatchTraktLists(cancellationToken);

            await RefreshGraphicsElements(cancellationToken);

            await ReleaseMemory(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during SchedulerService.{Method}", nameof(DoWork));
        }
    }

    private async Task ResetPlayouts(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            List<Playout> playouts = await dbContext.Playouts
                .AsNoTracking()
                .Filter(p => p.DailyRebuildTime != null)
                .Include(p => p.Channel)
                .ToListAsync(cancellationToken);

            foreach (Playout playout in playouts.OrderBy(p => decimal.Parse(
                         p.Channel.Number,
                         CultureInfo.InvariantCulture)))
            {
                DateTime now = DateTime.Now;
                DateTime target = DateTime.Today.Add(playout.DailyRebuildTime ?? TimeSpan.FromDays(7));
                // check absolute diff
                if (now.Subtract(target).Duration() < TimeSpan.FromMinutes(5))
                {
                    await _workerChannel.WriteAsync(
                        new BuildPlayout(playout.Id, PlayoutBuildMode.Reset),
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing resets for all daily rebuild playouts");
        }
    }

    private async Task BuildPlayouts(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            List<Playout> playouts = await dbContext.Playouts
                .AsNoTracking()
                .Include(p => p.Channel)
                .ToListAsync(cancellationToken);

            foreach (Playout playout in playouts.OrderBy(p => decimal.Parse(
                         p.Channel.Number,
                         CultureInfo.InvariantCulture)))
            {
                await _workerChannel.WriteAsync(
                    new BuildPlayout(playout.Id, PlayoutBuildMode.Continue),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing builds for all playouts");
        }
    }

    private async ValueTask RefreshChannelGuideChannelList(CancellationToken cancellationToken)
    {
        try
        {
            await _workerChannel.WriteAsync(new RefreshChannelList(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error queueing message in SchedulerService.{Method}",
                nameof(RefreshChannelGuideChannelList));
        }
    }

    private async Task ScanLocalMediaSources(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            foreach (int libraryId in dbContext.LocalMediaSources.SelectMany(ms => ms.Libraries).Map(l => l.Id))
            {
                if (_entityLocker.LockLibrary(libraryId))
                {
                    await _scannerWorkerChannel.WriteAsync(new ScanLocalLibraryIfNeeded(libraryId), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing scans for all local media sources");
        }
    }

    private async Task ScanPlexMediaSources(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            var mediaSourceIds = new System.Collections.Generic.HashSet<int>();

            // servers that plex.tv no longer lists cannot be reached, so don't queue scans for them
            List<int> missingMediaSourceIds = await dbContext.PlexMediaSources
                .AsNoTracking()
                .Filter(s => s.MissingSince != null)
                .Map(s => s.Id)
                .ToListAsync(cancellationToken);

            foreach (PlexLibrary library in dbContext.PlexLibraries.AsNoTracking().Filter(l => l.ShouldSyncItems))
            {
                if (missingMediaSourceIds.Contains(library.MediaSourceId))
                {
                    continue;
                }

                mediaSourceIds.Add(library.MediaSourceId);

                if (_entityLocker.LockLibrary(library.Id))
                {
                    await _scannerWorkerChannel.WriteAsync(
                        new SynchronizePlexLibraryByIdIfNeeded(library.Id),
                        cancellationToken);

                    if (library.MediaKind is LibraryMediaKind.Shows)
                    {
                        await _scannerWorkerChannel.WriteAsync(
                            new SynchronizePlexNetworks(library.Id, false),
                            cancellationToken);
                    }
                }
            }

            foreach (int mediaSourceId in mediaSourceIds)
            {
                await _scannerWorkerChannel.WriteAsync(
                    new SynchronizePlexCollections(mediaSourceId, false, false),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing scans for all plex media sources");
        }
    }

    private async Task ScanJellyfinMediaSources(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            var mediaSourceIds = new System.Collections.Generic.HashSet<int>();

            foreach (JellyfinLibrary library in dbContext.JellyfinLibraries.AsNoTracking()
                         .Filter(l => l.ShouldSyncItems))
            {
                mediaSourceIds.Add(library.MediaSourceId);

                if (_entityLocker.LockLibrary(library.Id))
                {
                    await _scannerWorkerChannel.WriteAsync(
                        new SynchronizeJellyfinLibraryByIdIfNeeded(library.Id),
                        cancellationToken);
                }
            }

            foreach (int mediaSourceId in mediaSourceIds)
            {
                await _scannerWorkerChannel.WriteAsync(
                    new SynchronizeJellyfinCollections(mediaSourceId, false, false),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing scans for all jellyfin media sources");
        }
    }

    private async Task ScanEmbyMediaSources(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            var mediaSourceIds = new System.Collections.Generic.HashSet<int>();

            foreach (EmbyLibrary library in dbContext.EmbyLibraries.AsNoTracking().Filter(l => l.ShouldSyncItems))
            {
                mediaSourceIds.Add(library.MediaSourceId);

                if (_entityLocker.LockLibrary(library.Id))
                {
                    await _scannerWorkerChannel.WriteAsync(
                        new SynchronizeEmbyLibraryByIdIfNeeded(library.Id),
                        cancellationToken);
                }
            }

            foreach (int mediaSourceId in mediaSourceIds)
            {
                await _scannerWorkerChannel.WriteAsync(
                    new SynchronizeEmbyCollections(mediaSourceId, false, false),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing scans for all emby media sources");
        }
    }

    private async Task RefreshTraktLists(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            DateTime target = DateTime.UtcNow.AddDays(-1);

            List<TraktList> traktLists = await dbContext.TraktLists
                .AsNoTracking()
                .Filter(tl => tl.AutoRefresh && (tl.LastUpdate == null || tl.LastUpdate <= target))
                .ToListAsync(cancellationToken);

            if (traktLists.Count != 0 && _entityLocker.LockTrakt())
            {
                TraktList last = traktLists.Last();
                foreach (TraktList list in traktLists)
                {
                    await _workerChannel.WriteAsync(
                        AddTraktList.Existing(list.User, list.List, list == last),
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing refreshes for all trakt lists");
        }
    }

    private async Task MatchTraktLists(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            DateTime target = DateTime.UtcNow.AddHours(-1);

            List<TraktList> traktLists = await dbContext.TraktLists
                .AsNoTracking()
                .Filter(tl => tl.LastMatch == null || tl.LastMatch <= target)
                .ToListAsync(cancellationToken);

            if (traktLists.Count != 0 && _entityLocker.LockTrakt())
            {
                TraktList last = traktLists.Last();
                foreach (TraktList list in traktLists)
                {
                    await _workerChannel.WriteAsync(
                        new MatchTraktListItems(list.Id, list == last),
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing matches for all trakt lists");
        }
    }

    private async Task RefreshMpegTsScripts(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IMpegTsScriptService>();
            await service.RefreshScripts(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing mpeg-ts scripts");
        }
    }

    private async ValueTask RefreshGraphicsElements(CancellationToken cancellationToken)
    {
        try
        {
            await _workerChannel.WriteAsync(new RefreshGraphicsElements(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error queueing message in SchedulerService.{Method}",
                nameof(RefreshGraphicsElements));
        }
    }

    private async ValueTask DeleteOrphanedSubtitles(CancellationToken cancellationToken)
    {
        try
        {
            await _workerChannel.WriteAsync(new DeleteOrphanedSubtitles(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error queueing message in SchedulerService.{Method}",
                nameof(DeleteOrphanedSubtitles));
        }
    }

    private async ValueTask DeleteOrphanedArtwork(CancellationToken cancellationToken)
    {
        try
        {
            await _workerChannel.WriteAsync(new DeleteOrphanedArtwork(100_000), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error queueing message in SchedulerService.{Method}",
                nameof(DeleteOrphanedArtwork));
        }
    }

    private async ValueTask ReleaseMemory(CancellationToken cancellationToken)
    {
        try
        {
            await _workerChannel.WriteAsync(new ReleaseMemory(false), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error queueing message in SchedulerService.{Method}",
                nameof(ReleaseMemory));
        }
    }

    private async ValueTask QueueFFmpegCapabilitiesRefresh(CancellationToken cancellationToken)
    {
        try
        {
            await _workerChannel.WriteAsync(new RefreshFFmpegCapabilities(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error queueing message in SchedulerService.{Method}",
                nameof(QueueFFmpegCapabilitiesRefresh));
        }
    }

    private async Task SyncAllNextPlayouts(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            TvContext dbContext = scope.ServiceProvider.GetRequiredService<TvContext>();

            List<Core.Domain.Channel> channels = await dbContext.Channels
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            foreach (Core.Domain.Channel channel in channels)
            {
                await _workerChannel.WriteAsync(new SyncNextPlayout(channel.Number), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error queueing synchronize for all next playouts");
        }
    }
}

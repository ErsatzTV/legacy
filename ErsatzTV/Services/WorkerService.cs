using System.Diagnostics;
using System.Threading.Channels;
using ErsatzTV.Application;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpeg;
using ErsatzTV.Application.Graphics;
using ErsatzTV.Application.Maintenance;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.Subtitles;
using ErsatzTV.Core;
using MediatR;

namespace ErsatzTV.Services;

public class WorkerService : BackgroundService
{
    private readonly ChannelReader<IBackgroundServiceRequest> _channel;
    private readonly ILogger<WorkerService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public WorkerService(
        ChannelReader<IBackgroundServiceRequest> channel,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<WorkerService> logger)
    {
        _channel = channel;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            _logger.LogInformation("Worker service started");

            await foreach (IBackgroundServiceRequest request in _channel.ReadAllAsync(stoppingToken))
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                using IServiceScope scope = _serviceScopeFactory.CreateScope();

                try
                {
                    IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                    switch (request)
                    {
                        case RefreshFFmpegCapabilities refreshFFmpegCapabilities:
                            await mediator.Send(refreshFFmpegCapabilities, stoppingToken);
                            break;
                        case RefreshChannelList refreshChannelList:
                            await mediator.Send(refreshChannelList, stoppingToken);
                            break;
                        case RefreshChannelData refreshChannelData:
                            await mediator.Send(refreshChannelData, stoppingToken);
                            break;
                        case BuildPlayout buildPlayout:
                        {
                            CancellationTokenSource cts = Debugger.IsAttached
                                ? new CancellationTokenSource(TimeSpan.FromMinutes(10))
                                : new CancellationTokenSource(TimeSpan.FromMinutes(2));

                            var linkedTokenSource =
                                CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stoppingToken);

                            Either<BaseError, Unit> buildPlayoutResult = await mediator.Send(
                                buildPlayout,
                                linkedTokenSource.Token);
                            buildPlayoutResult.BiIter(
                                _ => _logger.LogDebug("Built playout {PlayoutId}", buildPlayout.PlayoutId),
                                error => _logger.LogWarning(
                                    "Unable to build playout {PlayoutId}: {Error}",
                                    buildPlayout.PlayoutId,
                                    error.Value));
                            break;
                        }
                        case SyncNextPlayout syncNextPlayout:
                            await mediator.Send(syncNextPlayout, stoppingToken);
                            break;
                        case CheckForOverlappingPlayoutItems checkForOverlappingPlayoutItems:
                            await mediator.Send(checkForOverlappingPlayoutItems, stoppingToken);
                            break;
                        case TimeShiftOnDemandPlayout timeShiftOnDemandPlayout:
                            await mediator.Send(timeShiftOnDemandPlayout, stoppingToken);
                            break;
                        case DeleteOrphanedArtwork deleteOrphanedArtwork:
                            await mediator.Send(deleteOrphanedArtwork, stoppingToken);
                            break;
                        case DeleteOrphanedSubtitles deleteOrphanedSubtitles:
                            await mediator.Send(deleteOrphanedSubtitles, stoppingToken);
                            break;
                        case AddTraktList addTraktList:
                            Either<BaseError, Unit> result = await mediator.Send(addTraktList, stoppingToken);
                            foreach (BaseError error in result.LeftToSeq())
                            {
                                _logger.LogWarning(
                                    "Unable to add trakt list {Url}: {Error}",
                                    addTraktList.TraktListUrl,
                                    error.Value);
                            }

                            break;
                        case DeleteTraktList deleteTraktList:
                            await mediator.Send(deleteTraktList, stoppingToken);
                            break;
                        case MatchTraktListItems matchTraktListItems:
                            await mediator.Send(matchTraktListItems, stoppingToken);
                            break;
                        case RefreshGraphicsElements refreshGraphicsElements:
                            await mediator.Send(refreshGraphicsElements, stoppingToken);
                            break;
#if !DEBUG_NO_SYNC
                        case ExtractEmbeddedSubtitles extractEmbeddedSubtitles:
                            await mediator.Send(extractEmbeddedSubtitles, stoppingToken);
                            break;
                        case ExtractEmbeddedShowSubtitles extractEmbeddedShowSubtitles:
                            await mediator.Send(extractEmbeddedShowSubtitles, stoppingToken);
                            break;
#endif
                        case ReleaseMemory aggressivelyReleaseMemory:
                            await mediator.Send(aggressivelyReleaseMemory, stoppingToken);
                            break;
                    }
                }
                catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
                {
                    // this can happen when we're shutting down
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process background service request");
                }
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogInformation("Worker service shutting down");
        }
    }
}

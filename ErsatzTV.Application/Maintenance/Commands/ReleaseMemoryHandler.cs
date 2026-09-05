using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Maintenance;

public class ReleaseMemoryHandler : IRequestHandler<ReleaseMemory>
{
    private static long _lastRelease;

    private readonly IFFmpegSegmenterService _ffmpegSegmenterService;
    private readonly ILogger<ReleaseMemoryHandler> _logger;

    public ReleaseMemoryHandler(
        IFFmpegSegmenterService ffmpegSegmenterService,
        ILogger<ReleaseMemoryHandler> logger)
    {
        _ffmpegSegmenterService = ffmpegSegmenterService;
        _logger = logger;
    }

    public Task Handle(ReleaseMemory request, CancellationToken cancellationToken)
    {
        if (!request.ForceAggressive && _lastRelease > request.RequestTime.Ticks)
        {
            // we've already released since the request was created, so don't bother
            return Task.CompletedTask;
        }

        bool hasActiveWorkers = _ffmpegSegmenterService.Workers.Count > 0 || FFmpegProcess.ProcessCount > 0;
        if (request.ForceAggressive || !hasActiveWorkers)
        {
            _logger.LogDebug("Starting aggressive garbage collection");
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
        }
        else
        {
            _logger.LogDebug("Starting garbage collection");
            GC.Collect(2, GCCollectionMode.Forced, false);
        }

        GC.WaitForPendingFinalizers();
        GC.Collect();

        _logger.LogDebug("Completed garbage collection");
        Interlocked.Exchange(ref _lastRelease, DateTimeOffset.Now.Ticks);

        return Task.CompletedTask;
    }
}

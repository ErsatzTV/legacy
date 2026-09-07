using ErsatzTV.Core.Health;
using ErsatzTV.Core.Health.Checks;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Health;

public class HealthCheckService(
    IServiceScopeFactory serviceScopeFactory,
    IMemoryCache memoryCache,
    ILogger<HealthCheckService> logger) : IHealthCheckService
{
    private const string CacheKey = "healthcheck.summary";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    // this order is also the display order
    private static readonly Type[] CheckTypes =
    [
        typeof(IDowngradeHealthCheck),
        typeof(IMacOsConfigFolderHealthCheck),
        typeof(IUnifiedDockerHealthCheck),
        typeof(IFFmpegVersionHealthCheck),
        typeof(IFFmpegCapabilitiesHealthCheck),
        typeof(IFFmpegReportsHealthCheck),
        typeof(IHardwareAccelerationHealthCheck),
        typeof(IMovieMetadataHealthCheck),
        typeof(IEpisodeMetadataHealthCheck),
        typeof(IZeroDurationHealthCheck),
        typeof(IFileNotFoundHealthCheck),
        typeof(IUnavailableHealthCheck),
        typeof(IEmptyScheduleHealthCheck),
        typeof(IVaapiDriverHealthCheck)
    ];

    private readonly Lock _sync = new();

    private Task<List<HealthCheckResult>> _inFlight;
    private List<HealthCheckResult> _results;
    private DateTimeOffset _resultsExpireAt;

    public Task<List<HealthCheckResult>> GetCachedHealthChecks(CancellationToken cancellationToken) =>
        Run(bypassCache: false, cancellationToken);

    public Task<List<HealthCheckResult>> PerformHealthChecks(CancellationToken cancellationToken) =>
        Run(bypassCache: true, cancellationToken);

    public HealthCheckSummary GetHealthCheckSummary() =>
        memoryCache.Get<HealthCheckSummary>(CacheKey) ?? new HealthCheckSummary(0, 0);

    private async Task<List<HealthCheckResult>> Run(bool bypassCache, CancellationToken cancellationToken)
    {
        Task<List<HealthCheckResult>> run;

        lock (_sync)
        {
            if (!bypassCache && _results is not null && DateTimeOffset.UtcNow < _resultsExpireAt)
            {
                return _results;
            }

            // only one run at a time; two runs at once start ffmpeg twice, and can finish
            // out of order and store stale results
            if (_inFlight is null || _inFlight.IsCompleted)
            {
                _inFlight = RunChecks();
            }

            run = _inFlight;
        }

        return await run.WaitAsync(cancellationToken);
    }

    private async Task<List<HealthCheckResult>> RunChecks()
    {
        // this token is not the caller's; the checks start ffmpeg and read the whole media
        // library, so a closed page must not stop a run that other callers share
        using var cts = new CancellationTokenSource(RunTimeout);
        CancellationToken cancellationToken = cts.Token;

        using IServiceScope scope = serviceScopeFactory.CreateScope();

        List<HealthCheckResult> results = await CheckTypes
            .Map(t => (IHealthCheck)scope.ServiceProvider.GetRequiredService(t))
            .Map(c =>
            {
                var failedResult = new HealthCheckResult(
                    c.Title,
                    HealthCheckStatus.Fail,
                    "Health check failure; see logs",
                    "Health check failure",
                    None);
                return TryAsync(() => c.Check(cancellationToken)).IfFail(ex => LogAndReturn(ex, failedResult));
            })
            .SequenceParallel()
            .Map(r => r.ToList());

        if (cancellationToken.IsCancellationRequested)
        {
            // after a timeout each unfinished check reports a failure that is not real, so do not
            // cache or publish it; an exception here stops the host through RunHealthChecksService
            logger.LogWarning("Health checks did not complete within {Timeout}", RunTimeout);
            return results;
        }

        var summary = new HealthCheckSummary(
            results.Count(x => x.Status is HealthCheckStatus.Warning),
            results.Count(x => x.Status is HealthCheckStatus.Fail));

        lock (_sync)
        {
            _results = results;
            _resultsExpireAt = DateTimeOffset.UtcNow.Add(CacheDuration);
        }

        memoryCache.Set(CacheKey, summary);

        try
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Publish(summary, cancellationToken);
        }
        catch (Exception ex)
        {
            // an exception from a notification handler must not stop the host
            logger.LogWarning(ex, "Failed to publish health check summary");
        }

        return results;
    }

    private HealthCheckResult LogAndReturn(Exception ex, HealthCheckResult failedResult)
    {
        if (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to run health check {Title}", failedResult.Title);
        }

        return failedResult;
    }
}

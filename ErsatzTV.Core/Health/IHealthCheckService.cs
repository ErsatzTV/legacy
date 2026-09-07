namespace ErsatzTV.Core.Health;

public interface IHealthCheckService
{
    Task<List<HealthCheckResult>> PerformHealthChecks(CancellationToken cancellationToken);
    Task<List<HealthCheckResult>> GetCachedHealthChecks(CancellationToken cancellationToken);
    HealthCheckSummary GetHealthCheckSummary();
}

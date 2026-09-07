using ErsatzTV.Core.Health;

namespace ErsatzTV.Application.Health;

public class GetAllHealthCheckResultsHandler(IHealthCheckService healthCheckService)
    : IRequestHandler<GetAllHealthCheckResults, List<HealthCheckResult>>
{
    public async Task<List<HealthCheckResult>> Handle(
        GetAllHealthCheckResults request,
        CancellationToken cancellationToken)
    {
        try
        {
            List<HealthCheckResult> results = request.Refresh
                ? await healthCheckService.PerformHealthChecks(cancellationToken)
                : await healthCheckService.GetCachedHealthChecks(cancellationToken);
            return results.Filter(r => r.Status != HealthCheckStatus.NotApplicable).ToList();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return [];
        }
    }
}

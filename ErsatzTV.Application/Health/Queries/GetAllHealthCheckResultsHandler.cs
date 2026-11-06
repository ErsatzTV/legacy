using ErsatzTV.Application.Streaming;
using ErsatzTV.Core.Health;

namespace ErsatzTV.Application.Health;

public class GetAllHealthCheckResultsHandler(IHealthCheckService healthCheckService, IMediator mediator)
    : IRequestHandler<GetAllHealthCheckResults, List<HealthCheckResult>>
{
    public async Task<List<HealthCheckResult>> Handle(
        GetAllHealthCheckResults request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.Refresh)
            {
                await mediator.Send(new RefreshNextProbe(), cancellationToken);
            }

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

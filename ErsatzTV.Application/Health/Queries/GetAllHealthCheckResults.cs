using ErsatzTV.Core.Health;

namespace ErsatzTV.Application.Health;

public record GetAllHealthCheckResults(bool Refresh) : IRequest<List<HealthCheckResult>>;

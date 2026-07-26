namespace ErsatzTV.Application.Troubleshooting.Queries;

public record GetTroubleshootingInfo(string NextVersion) : IRequest<TroubleshootingInfo>;

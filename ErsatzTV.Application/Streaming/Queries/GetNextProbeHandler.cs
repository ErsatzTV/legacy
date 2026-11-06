using ErsatzTV.Core.Interfaces.Streaming;

namespace ErsatzTV.Application.Streaming;

public class GetNextProbeHandler(INextProbeService nextProbeService)
    : IRequestHandler<GetNextProbe, Option<NextProbeResult>>
{
    public Task<Option<NextProbeResult>> Handle(GetNextProbe request, CancellationToken cancellationToken) =>
        Task.FromResult(nextProbeService.Latest);
}

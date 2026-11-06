using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Streaming;

namespace ErsatzTV.Application.Streaming;

public class RefreshNextProbeHandler(
    INextProbeService nextProbeService,
    IConfigElementRepository configElementRepository)
    : IRequestHandler<RefreshNextProbe, Option<NextProbeResult>>
{
    public async Task<Option<NextProbeResult>> Handle(RefreshNextProbe request, CancellationToken cancellationToken)
    {
        Option<string> maybeFFmpegPath = await configElementRepository.GetValue<string>(
            ConfigElementKey.FFmpegPath,
            cancellationToken);

        foreach (string ffmpegPath in maybeFFmpegPath)
        {
            return await nextProbeService.Refresh(ffmpegPath, cancellationToken);
        }

        return nextProbeService.Latest;
    }
}

using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.FFmpeg;

public class RefreshFFmpegCapabilitiesHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IHardwareCapabilitiesFactory hardwareCapabilitiesFactory,
    ILocalStatisticsProvider localStatisticsProvider,
    INextProbeService nextProbeService)
    : IRequestHandler<RefreshFFmpegCapabilities>
{
    public async Task Handle(RefreshFFmpegCapabilities request, CancellationToken cancellationToken)
    {
        hardwareCapabilitiesFactory.ClearCache();

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<string> maybeFFmpegPath = await dbContext.ConfigElements
            .GetValue<string>(ConfigElementKey.FFmpegPath, cancellationToken)
            .FilterT(File.Exists);

        foreach (string ffmpegPath in maybeFFmpegPath)
        {
            // not awaited: a slow probe would block the worker queue
            _ = nextProbeService.Refresh(ffmpegPath, CancellationToken.None);

            _ = await hardwareCapabilitiesFactory.GetFFmpegCapabilities(ffmpegPath, cancellationToken);

            Option<string> maybeFFprobePath = await dbContext.ConfigElements
                .GetValue<string>(ConfigElementKey.FFprobePath, cancellationToken)
                .FilterT(File.Exists);

            foreach (string ffprobePath in maybeFFprobePath)
            {
                Either<BaseError, MediaVersion> result = await localStatisticsProvider.GetStatistics(
                    ffprobePath,
                    Path.Combine(FileSystemLayout.ResourcesCacheFolder, "test.avs"));

                hardwareCapabilitiesFactory.SetAviSynthInstalled(result.IsRight);
            }
        }
    }
}

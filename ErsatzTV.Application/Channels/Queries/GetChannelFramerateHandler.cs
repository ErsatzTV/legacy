using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Channels;

public class GetChannelFramerateHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ILogger<GetChannelFramerateHandler> logger)
    : IRequestHandler<GetChannelFramerate, Option<FrameRate>>
{
    public async Task<Option<FrameRate>> Handle(GetChannelFramerate request, CancellationToken cancellationToken)
    {
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            FFmpegProfile ffmpegProfile = await dbContext.Channels
                .AsNoTracking()
                .Filter(c => c.Number == request.ChannelNumber)
                .Include(c => c.FFmpegProfile)
                .Map(c => c.FFmpegProfile)
                .SingleAsync(cancellationToken);

            if (!request.IgnoreProfile && !ffmpegProfile.NormalizeFramerate)
            {
                return Option<FrameRate>.None;
            }

            // TODO: expand to check everything in collection rather than what's scheduled?
            logger.LogDebug("Checking frame rates for channel {ChannelNumber}", request.ChannelNumber);

            List<Playout> playouts = await dbContext.Playouts
                .AsNoTracking()
                .Include(p => p.Items)
                .ThenInclude(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as Movie).MediaVersions)
                .Include(p => p.Items)
                .ThenInclude(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as Episode).MediaVersions)
                .Include(p => p.Items)
                .ThenInclude(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as Song).MediaVersions)
                .Include(p => p.Items)
                .ThenInclude(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
                .Include(p => p.Items)
                .ThenInclude(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as OtherVideo).MediaVersions)
                .Include(p => p.Items)
                .ThenInclude(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as Image).MediaVersions)
                .Include(p => p.Items)
                .ThenInclude(pi => pi.MediaItem)
                .ThenInclude(mi => (mi as RemoteStream).MediaVersions)
                .Filter(p => p.Channel.Number == request.ChannelNumber)
                .ToListAsync(cancellationToken);

            var frameRates = playouts.Map(p => p.Items.Map(i => i.MediaItem.GetHeadVersion()))
                .Flatten()
                .Map(mv => new FrameRate(mv.RFrameRate))
                .ToList();

            var distinct = frameRates.Distinct().ToList();
            if (distinct.Count > 1)
            {
                // TODO: something more intelligent than minimum framerate?
                var validFrameRates = frameRates.Where(fr => fr.ParsedFrameRate > 23).ToList();
                if (validFrameRates.Count > 0)
                {
                    FrameRate result = validFrameRates.MinBy(fr => fr.ParsedFrameRate);
                    logger.LogInformation(
                        "Normalizing frame rate for channel {ChannelNumber} from {Distinct} to {FrameRate}",
                        request.ChannelNumber,
                        distinct.Map(fr => fr.RFrameRate),
                        result.RFrameRate);
                    return result;
                }

                FrameRate minFrameRate = frameRates.MinBy(fr => fr.ParsedFrameRate);
                logger.LogInformation(
                    "Normalizing frame rate for channel {ChannelNumber} from {Distinct} to {FrameRate} instead of min value {MinFrameRate}",
                    request.ChannelNumber,
                    distinct.Map(fr => fr.RFrameRate),
                    FrameRate.DefaultFrameRate.RFrameRate,
                    minFrameRate.RFrameRate);

                return FrameRate.DefaultFrameRate;
            }

            if (distinct.Count != 0)
            {
                logger.LogInformation(
                    "All content on channel {ChannelNumber} has the same frame rate of {FrameRate}; will not normalize",
                    request.ChannelNumber,
                    distinct[0].RFrameRate);
            }
            else
            {
                logger.LogInformation(
                    "No content on channel {ChannelNumber} has frame rate information; will not normalize",
                    request.ChannelNumber);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Unexpected error checking frame rates on channel {ChannelNumber}",
                request.ChannelNumber);
        }

        return None;
    }
}

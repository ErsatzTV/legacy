using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Scheduling;

public class PlayoutTimeShifter(
    IDbContextFactory<TvContext> dbContextFactory,
    IFFmpegSegmenterService segmenterService,
    ILogger<PlayoutTimeShifter> logger)
    : IPlayoutTimeShifter
{
    public async Task TimeShift(int playoutId, DateTimeOffset now, bool force, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Channel> maybeChannel = await dbContext.Playouts
            .AsNoTracking()
            .Include(p => p.Channel)
            .SelectOneAsync(p => p.Id, p => p.Id == playoutId, cancellationToken)
            .MapT(p => p.Channel);

        foreach (Channel channel in maybeChannel.Where(c => c.PlayoutMode is ChannelPlayoutMode.OnDemand))
        {
            Option<Playout> maybePlayout = await dbContext.Playouts
                .Include(p => p.Channel)
                .Include(p => p.Items)
                .Include(p => p.Anchor)
                .Include(p => p.ProgramScheduleAnchors)
                .Include(p => p.PlayoutHistory)
                .SelectOneAsync(p => p.ChannelId, p => p.ChannelId == channel.Id, cancellationToken);

            foreach (Playout playout in maybePlayout)
            {
                if (playout.Channel.PlayoutMode is not ChannelPlayoutMode.OnDemand)
                {
                    return;
                }

                if (!force && segmenterService.IsActive(playout.Channel.Number))
                {
                    logger.LogDebug(
                        "Will not time shift on demand playout that is active for channel {Number} - {Name}",
                        playout.Channel.Number,
                        playout.Channel.Name);

                    return;
                }

                if (playout.Items.Count == 0)
                {
                    logger.LogDebug(
                        "Unable to time shift empty playout for channel {Number} - {Name}",
                        playout.Channel.Number,
                        playout.Channel.Name);

                    return;
                }

                if (playout.OnDemandCheckpoint is null)
                {
                    logger.LogDebug(
                        "Time shifting unwatched playout for channel {Number} - {Name}",
                        playout.Channel.Number,
                        playout.Channel.Name);

                    playout.OnDemandCheckpoint = playout.Items.Min(p => p.StartOffset);
                }

                // remove items that have finished before the checkpoint
                foreach (DateTimeOffset checkpoint in Optional(playout.OnDemandCheckpoint))
                {
                    DateTime checkpointUtc = checkpoint.UtcDateTime;
                    int removed = playout.Items.RemoveAll(i => i.Finish < checkpointUtc);
                    if (removed > 0)
                    {
                        logger.LogDebug(
                            "Removing {Count} played items before shifting playout for channel {Number} - {Name}",
                            removed,
                            playout.Channel.Number,
                            playout.Channel.Name);
                    }
                }

                TimeSpan toOffset = now - playout.OnDemandCheckpoint.IfNone(now);

                logger.LogDebug(
                    "Time shifting playout for channel {Number} - {Name} forward by {Time}",
                    playout.Channel.Number,
                    playout.Channel.Name,
                    toOffset);

                // time shift history
                foreach (PlayoutHistory history in playout.PlayoutHistory)
                {
                    history.When += toOffset;
                    history.Finish += toOffset;
                }

                // time shift items
                foreach (PlayoutItem playoutItem in playout.Items)
                {
                    playoutItem.Start += toOffset;
                    playoutItem.Finish += toOffset;

                    if (playoutItem.GuideStart.HasValue)
                    {
                        playoutItem.GuideStart += toOffset;
                    }

                    if (playoutItem.GuideFinish.HasValue)
                    {
                        playoutItem.GuideFinish += toOffset;
                    }
                }

                // time shift anchors
                foreach (PlayoutProgramScheduleAnchor anchor in playout.ProgramScheduleAnchors)
                {
                    if (anchor.AnchorDate.HasValue)
                    {
                        anchor.AnchorDate += toOffset;
                    }
                }

                // time shift anchor
                if (playout.Anchor is not null)
                {
                    playout.Anchor.NextStart += toOffset;
                }

                playout.OnDemandCheckpoint = now;

                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }
}

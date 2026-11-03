using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Scheduling;

public class ReplacePlayoutTemplateItemsHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> workerChannel,
    ILogger<ReplacePlayoutTemplateItemsHandler> logger)
    : IRequestHandler<ReplacePlayoutTemplateItems, Option<BaseError>>
{
    public async Task<Option<BaseError>> Handle(
        ReplacePlayoutTemplateItems request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            Option<Playout> maybePlayout = await dbContext.Playouts
                .Include(p => p.ProgramSchedule)
                .Include(p => p.Templates)
                .ThenInclude(t => t.Template)
                .SelectOneAsync(p => p.Id, p => p.Id == request.PlayoutId, cancellationToken);

            foreach (Playout playout in maybePlayout)
            {
                PlayoutTemplate[] existing = playout.Templates.ToArray();

                List<ReplacePlayoutTemplate> incoming = request.Items;

                var toAdd = incoming.Filter(x => existing.All(e => e.Id != x.Id)).ToList();
                var toRemove = existing.Filter(e => incoming.All(m => m.Id != e.Id)).ToList();
                var toUpdate = incoming.Except(toAdd).ToList();

                foreach (PlayoutTemplate remove in toRemove)
                {
                    playout.Templates.Remove(remove);
                }

                DateTime now = DateTime.UtcNow;

                foreach (ReplacePlayoutTemplate add in toAdd)
                {
                    playout.Templates.Add(
                        new PlayoutTemplate
                        {
                            PlayoutId = playout.Id,
                            Index = add.Index,
                            TemplateId = add.TemplateId,
                            DecoTemplateId = add.DecoTemplateId,
                            DaysOfWeek = add.DaysOfWeek,
                            DaysOfMonth = add.DaysOfMonth,
                            MonthsOfYear = add.MonthsOfYear,
                            LimitToDateRange = add.LimitToDateRange,
                            StartMonth = add.StartMonth,
                            StartDay = add.StartDay,
                            StartYear = add.StartYear,
                            EndMonth = add.EndMonth,
                            EndDay = add.EndDay,
                            EndYear = add.EndYear,
                            DateUpdated = now
                        });
                }

                foreach (ReplacePlayoutTemplate update in toUpdate)
                {
                    foreach (PlayoutTemplate ex in existing.Filter(x => x.Id == update.Id))
                    {
                        ex.Index = update.Index;
                        ex.TemplateId = update.TemplateId;
                        ex.DecoTemplateId = update.DecoTemplateId;
                        ex.DaysOfWeek = update.DaysOfWeek;
                        ex.DaysOfMonth = update.DaysOfMonth;
                        ex.MonthsOfYear = update.MonthsOfYear;
                        ex.LimitToDateRange = update.LimitToDateRange;
                        ex.StartMonth = update.StartMonth;
                        ex.StartDay = update.StartDay;
                        ex.StartYear = update.StartYear;
                        ex.EndMonth = update.EndMonth;
                        ex.EndDay = update.EndDay;
                        ex.EndYear = update.EndYear;
                        ex.DateUpdated = now;
                    }
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                // deco templates apply at serialization, not at build
                List<string> channelNumbers = await NextPlayoutDependents.ForPlayout(
                    dbContext,
                    playout.Id,
                    cancellationToken);
                await workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);
            }

            return Option<BaseError>.None;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving playout template items");
            return BaseError.New(ex.Message);
        }
    }
}

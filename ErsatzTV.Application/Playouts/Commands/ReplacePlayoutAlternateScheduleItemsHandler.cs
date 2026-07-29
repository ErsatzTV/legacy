using System.Threading.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Playouts;

public class ReplacePlayoutAlternateScheduleItemsHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> channel,
    ILogger<ReplacePlayoutAlternateScheduleItemsHandler> logger)
    :
        IRequestHandler<ReplacePlayoutAlternateScheduleItems, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        ReplacePlayoutAlternateScheduleItems request,
        CancellationToken cancellationToken)
    {
        if (request.Items.Count == 0)
        {
            return BaseError.New("Playout alternate schedule items must not be empty");
        }

        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            Option<Playout> maybePlayout = await dbContext.Playouts
                .Include(p => p.ProgramSchedule)
                .Include(p => p.ProgramScheduleAlternates)
                .ThenInclude(p => p.ProgramSchedule)
                .SelectOneAsync(p => p.Id, p => p.Id == request.PlayoutId, cancellationToken);

            foreach (Playout playout in maybePlayout)
            {
                var existingScheduleMap = new Dictionary<DateTimeOffset, ProgramSchedule>();
                var daysToCheck = new List<DateTimeOffset>();

                Option<PlayoutItem> maybeLastPlayoutItem = await dbContext.PlayoutItems
                    .Filter(pi => pi.PlayoutId == request.PlayoutId)
                    .OrderByDescending(pi => pi.Start)
                    .FirstOrDefaultAsync(cancellationToken)
                    .Map(Optional);

                foreach (PlayoutItem lastPlayoutItem in maybeLastPlayoutItem)
                {
                    DateTimeOffset start = DateTimeOffset.Now;
                    daysToCheck = Enumerable.Range(0, (lastPlayoutItem.StartOffset - start).Days + 1)
                        .Select(d => start.AddDays(d))
                        .ToList();

                    foreach (DateTimeOffset dayToCheck in daysToCheck)
                    {
                        ProgramSchedule schedule = AlternateScheduleSelector
                            .GetScheduleForDate(playout.ProgramScheduleAlternates, dayToCheck)
                            .Match(s => s.ProgramSchedule, playout.ProgramSchedule);

                        existingScheduleMap.Add(dayToCheck, schedule);
                    }
                }

                // exclude highest index
                int maxIndex = request.Items.Map(x => x.Index).Max();
                ReplacePlayoutAlternateSchedule highest = request.Items.First(x => x.Index == maxIndex);

                ProgramScheduleAlternate[] existing = playout.ProgramScheduleAlternates.ToArray();

                var incoming = request.Items.Except([highest]).ToList();

                var toAdd = incoming.Filter(x => existing.All(e => e.Id != x.Id)).ToList();
                var toRemove = existing.Filter(e => incoming.All(m => m.Id != e.Id)).ToList();
                var toUpdate = incoming.Except(toAdd).ToList();

                playout.ProgramScheduleAlternates.RemoveAll(toRemove.Contains);

                foreach (ReplacePlayoutAlternateSchedule add in toAdd)
                {
                    playout.ProgramScheduleAlternates.Add(
                        new ProgramScheduleAlternate
                        {
                            PlayoutId = playout.Id,
                            Index = add.Index,
                            ProgramScheduleId = add.ProgramScheduleId,
                            DaysOfWeek = add.DaysOfWeek,
                            DaysOfMonth = add.DaysOfMonth,
                            MonthsOfYear = add.MonthsOfYear,
                            LimitToDateRange = add.LimitToDateRange,
                            StartMonth = add.StartMonth,
                            StartDay = add.StartDay,
                            StartYear = add.StartYear,
                            EndMonth = add.EndMonth,
                            EndDay = add.EndDay,
                            EndYear = add.EndYear
                        });
                }

                foreach (ReplacePlayoutAlternateSchedule update in toUpdate)
                {
                    foreach (ProgramScheduleAlternate ex in existing.Filter(x => x.Id == update.Id))
                    {
                        ex.Index = update.Index;
                        ex.ProgramScheduleId = update.ProgramScheduleId;
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
                    }
                }

                // save highest index directly to playout
                bool hasDefaultScheduleChange = playout.ProgramScheduleId != highest.ProgramScheduleId;
                if (hasDefaultScheduleChange)
                {
                    playout.ProgramScheduleId = highest.ProgramScheduleId;
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                if (hasDefaultScheduleChange)
                {
                    await dbContext.Entry(playout).Reference(p => p.ProgramSchedule).LoadAsync(cancellationToken);
                }

                // load newly-added schedules
                foreach (ProgramScheduleAlternate alternate in playout.ProgramScheduleAlternates
                             .Where(alternate => alternate.ProgramSchedule is null))
                {
                    await dbContext.Entry(alternate).Reference(a => a.ProgramSchedule).LoadAsync(cancellationToken);
                }

                foreach (PlayoutItem _ in maybeLastPlayoutItem)
                {
                    foreach (DateTimeOffset dayToCheck in daysToCheck)
                    {
                        ProgramSchedule schedule = AlternateScheduleSelector
                            .GetScheduleForDate(playout.ProgramScheduleAlternates, dayToCheck)
                            .Match(s => s.ProgramSchedule, playout.ProgramSchedule);

                        if (existingScheduleMap.TryGetValue(dayToCheck, out ProgramSchedule existingValue) &&
                            existingValue.Id != schedule.Id)
                        {
                            logger.LogInformation(
                                "Alternate schedule change detected for day {Day}, schedule {One} => {Two}; will refresh playout",
                                dayToCheck,
                                existingValue.Name,
                                schedule.Name);

                            await channel.WriteAsync(
                                new BuildPlayout(request.PlayoutId, PlayoutBuildMode.Refresh),
                                cancellationToken);

                            break;
                        }
                    }
                }
            }

            return Unit.Default;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error saving alternate schedule items");
            return BaseError.New(ex.Message);
        }
    }
}

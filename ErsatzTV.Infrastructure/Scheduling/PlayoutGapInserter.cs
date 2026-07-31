using EFCore.BulkExtensions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Infrastructure.Scheduling;

public class PlayoutGapInserter(IDbContextFactory<TvContext> dbContextFactory) : IPlayoutGapInserter
{
    public async Task InsertGaps(int playoutId, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var toAdd = new List<PlayoutGap>();

        IOrderedQueryable<PlayoutItem> query = dbContext.PlayoutItems
            .AsNoTracking()
            .Filter(pi => pi.PlayoutId == playoutId)
            .OrderBy(i => i.Start);

        var queue = new Queue<PlayoutItem>(query);
        while (queue.Count > 1)
        {
            PlayoutItem one = queue.Dequeue();
            PlayoutItem two = queue.Peek();

            DateTime start = one.Finish;
            DateTime finish = two.Start;

            // overlapping items would otherwise produce a negative-duration gap
            if (start >= finish)
            {
                continue;
            }

            var gap = new PlayoutGap
            {
                PlayoutId = playoutId,
                Start = start,
                Finish = finish
            };

            toAdd.Add(gap);
        }

        // delete all existing gaps
        await dbContext.PlayoutGaps
            .Where(pg => pg.PlayoutId == playoutId)
            .ExecuteDeleteAsync(cancellationToken);

        // insert new gaps
        await dbContext.BulkInsertAsync(toAdd, cancellationToken: cancellationToken);
    }
}

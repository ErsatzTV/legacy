using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Scheduling;

public class UpdateDefaultDecoHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> workerChannel)
    : IRequestHandler<UpdateDefaultDeco, Option<BaseError>>
{
    public async Task<Option<BaseError>> Handle(UpdateDefaultDeco request, CancellationToken cancellationToken)
    {
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            await dbContext.Playouts
                .Where(p => p.Id == request.PlayoutId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.DecoId, p => request.DecoId), cancellationToken);

            List<string> channelNumbers = await NextPlayoutDependents.ForPlayout(
                dbContext,
                request.PlayoutId,
                cancellationToken);
            await workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);

            return Option<BaseError>.None;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }
}

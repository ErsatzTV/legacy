using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Scheduling;

public class DeleteDecoHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> workerChannel)
    : IRequestHandler<DeleteDeco, Option<BaseError>>
{
    public async Task<Option<BaseError>> Handle(DeleteDeco request, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Deco> maybeDeco = await dbContext.Decos
            .SelectOneAsync(p => p.Id, p => p.Id == request.DecoId, cancellationToken);

        foreach (Deco deco in maybeDeco)
        {
            List<string> channelNumbers = await NextPlayoutDependents.ForDeco(dbContext, deco.Id, cancellationToken);

            dbContext.Decos.Remove(deco);
            await dbContext.SaveChangesAsync(cancellationToken);

            await workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);
        }

        return maybeDeco.Match(
            _ => Option<BaseError>.None,
            () => BaseError.New($"Deco {request.DecoId} does not exist."));
    }
}

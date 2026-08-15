using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Playouts;

public class GetPlayoutModeByChannelNumberHandler(IDbContextFactory<TvContext> dbContextFactory)
    : IRequestHandler<GetPlayoutModeByChannelNumber, Option<PlayoutModeViewModel>>
{
    public async Task<Option<PlayoutModeViewModel>> Handle(
        GetPlayoutModeByChannelNumber request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.Playouts)
            .SingleOrDefaultAsync(c => c.Number == request.ChannelNumber, cancellationToken)
            .Map(Optional)
            .Map(Mapper.ProjectToModeViewModel);
    }
}

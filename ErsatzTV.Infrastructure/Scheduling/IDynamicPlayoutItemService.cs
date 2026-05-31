using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;

namespace ErsatzTV.Infrastructure.Scheduling;

public interface IDynamicPlayoutItemService
{
    Task<Either<BaseError, PlayoutItemWithPath>> CheckForFallbackFiller(
        TvContext dbContext,
        Channel channel,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Either<BaseError, PlayoutItemWithPath>> ValidatePlayoutItemPath(
        TvContext dbContext,
        PlayoutItem playoutItem,
        CancellationToken cancellationToken);
}

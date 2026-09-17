using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.MediaCollections;

public class DeleteSmartCollectionHandler : IRequestHandler<DeleteSmartCollection, Either<BaseError, Unit>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ISearchTargets _searchTargets;
    private readonly ISmartCollectionCache _smartCollectionCache;

    public DeleteSmartCollectionHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        ISearchTargets searchTargets,
        ISmartCollectionCache smartCollectionCache)
    {
        _dbContextFactory = dbContextFactory;
        _searchTargets = searchTargets;
        _smartCollectionCache = smartCollectionCache;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        DeleteSmartCollection request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, SmartCollection> validation = await SmartCollectionMustExist(
            dbContext,
            request,
            cancellationToken);
        return await validation.Apply(c => DoDeletion(dbContext, c, cancellationToken));
    }

    private async Task<Unit> DoDeletion(
        TvContext dbContext,
        SmartCollection smartCollection,
        CancellationToken cancellationToken)
    {
        dbContext.SmartCollections.Remove(smartCollection);
        await dbContext.SaveChangesAsync(cancellationToken);
        _searchTargets.SearchTargetsChanged();
        await _smartCollectionCache.Refresh(cancellationToken);
        return Unit.Default;
    }

    private static Task<Validation<BaseError, SmartCollection>> SmartCollectionMustExist(
        TvContext dbContext,
        DeleteSmartCollection request,
        CancellationToken cancellationToken) =>
        dbContext.SmartCollections
            .SelectOneAsync(c => c.Id, c => c.Id == request.SmartCollectionId, cancellationToken)
            .Map(o => o.ToValidation(
                BaseError.NotFound($"SmartCollection {request.SmartCollectionId} does not exist.")));
}

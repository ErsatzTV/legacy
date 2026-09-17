using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Search;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using static ErsatzTV.Application.MediaCollections.Mapper;

namespace ErsatzTV.Application.MediaCollections;

public class CreateSmartCollectionHandler :
    IRequestHandler<CreateSmartCollection, Either<BaseError, SmartCollectionViewModel>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ISearchTargets _searchTargets;
    private readonly ISmartCollectionCache _smartCollectionCache;

    public CreateSmartCollectionHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        ISearchTargets searchTargets,
        ISmartCollectionCache smartCollectionCache)
    {
        _dbContextFactory = dbContextFactory;
        _searchTargets = searchTargets;
        _smartCollectionCache = smartCollectionCache;
    }

    public async Task<Either<BaseError, SmartCollectionViewModel>> Handle(
        CreateSmartCollection request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, SmartCollection> validation = await Validate(dbContext, request);
        return await validation.Apply(c => PersistCollection(dbContext, c, cancellationToken));
    }

    private async Task<SmartCollectionViewModel> PersistCollection(
        TvContext dbContext,
        SmartCollection smartCollection,
        CancellationToken cancellationToken)
    {
        await dbContext.SmartCollections.AddAsync(smartCollection, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        _searchTargets.SearchTargetsChanged();
        await _smartCollectionCache.Refresh(cancellationToken);
        return ProjectToViewModel(smartCollection);
    }

    private static Task<Validation<BaseError, SmartCollection>> Validate(
        TvContext dbContext,
        CreateSmartCollection request) =>
        ValidateName(dbContext, request).MapT(name => new SmartCollection
        {
            Name = name,
            Query = request.Query
        });

    private static async Task<Validation<BaseError, string>> ValidateName(
        TvContext dbContext,
        CreateSmartCollection createSmartCollection)
    {
        bool duplicateName = await dbContext.SmartCollections
            .AnyAsync(c => c.Name == createSmartCollection.Name);

        Validation<BaseError, Unit> result2 = duplicateName
            ? Fail<BaseError, Unit>(BaseError.Conflict("SmartCollection name must be unique"))
            : Success<BaseError, Unit>(Unit.Default);

        Validation<BaseError, string> result1 = createSmartCollection.NotEmpty(c => c.Name)
            .Bind(_ => createSmartCollection.NotLongerThan(50)(c => c.Name));

        return (result1, result2).Apply((_, _) => createSmartCollection.Name);
    }
}

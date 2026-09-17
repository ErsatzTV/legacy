using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.MediaCollections;

public class
    UpdateSmartCollectionHandler : IRequestHandler<UpdateSmartCollection,
    Either<BaseError, UpdateSmartCollectionResult>>
{
    private readonly ChannelWriter<IBackgroundServiceRequest> _channel;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IMediaCollectionRepository _mediaCollectionRepository;
    private readonly ISearchTargets _searchTargets;
    private readonly ISmartCollectionCache _smartCollectionCache;

    public UpdateSmartCollectionHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        IMediaCollectionRepository mediaCollectionRepository,
        ChannelWriter<IBackgroundServiceRequest> channel,
        ISearchTargets searchTargets,
        ISmartCollectionCache smartCollectionCache)
    {
        _dbContextFactory = dbContextFactory;
        _mediaCollectionRepository = mediaCollectionRepository;
        _channel = channel;
        _searchTargets = searchTargets;
        _smartCollectionCache = smartCollectionCache;
    }

    public async Task<Either<BaseError, UpdateSmartCollectionResult>> Handle(
        UpdateSmartCollection request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, SmartCollection> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(c => ApplyUpdateRequest(dbContext, c, request, cancellationToken));
    }

    private async Task<UpdateSmartCollectionResult> ApplyUpdateRequest(
        TvContext dbContext,
        SmartCollection c,
        UpdateSmartCollection request,
        CancellationToken cancellationToken)
    {
        c.Query = request.Query;
        c.Name = request.Name;

        // rebuild playouts
        if (await dbContext.SaveChangesAsync(cancellationToken) > 0)
        {
            _searchTargets.SearchTargetsChanged();
            await _smartCollectionCache.Refresh(cancellationToken);

            // refresh all playouts that use this smart collection
            foreach (int playoutId in await _mediaCollectionRepository.PlayoutIdsUsingSmartCollection(request.Id))
            {
                await _channel.WriteAsync(new BuildPlayout(playoutId, PlayoutBuildMode.Refresh), cancellationToken);
            }
        }

        return new UpdateSmartCollectionResult(c.Id);
    }

    private static Task<Validation<BaseError, SmartCollection>> Validate(
        TvContext dbContext,
        UpdateSmartCollection request,
        CancellationToken cancellationToken) => ValidateName(dbContext, request)
        .BindT(_ => SmartCollectionMustExist(dbContext, request, cancellationToken));

    private static Task<Validation<BaseError, SmartCollection>> SmartCollectionMustExist(
        TvContext dbContext,
        UpdateSmartCollection updateCollection,
        CancellationToken cancellationToken) =>
        dbContext.SmartCollections
            .SelectOneAsync(c => c.Id, c => c.Id == updateCollection.Id, cancellationToken)
            .Map(o => o.ToValidation(BaseError.NotFound($"SmartCollection {updateCollection.Id} does not exist.")));

    private static async Task<Validation<BaseError, string>> ValidateName(
        TvContext dbContext,
        UpdateSmartCollection updateCollection)
    {
        Validation<BaseError, string> result1 = updateCollection.NotEmpty(c => c.Name)
            .Bind(_ => updateCollection.NotLongerThan(50)(c => c.Name));

        bool duplicateName = await dbContext.SmartCollections
            .AnyAsync(c => c.Id != updateCollection.Id && c.Name == updateCollection.Name);

        Validation<BaseError, Unit> result2 = duplicateName
            ? Fail<BaseError, Unit>(BaseError.BadRequest("SmartCollection name must be unique"))
            : Success<BaseError, Unit>(Unit.Default);

        return (result1, result2).Apply((_, _) => updateCollection.Name);
    }
}

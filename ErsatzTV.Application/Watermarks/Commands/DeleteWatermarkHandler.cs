using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Watermarks;

public class DeleteWatermarkHandler : IRequestHandler<DeleteWatermark, Either<BaseError, Unit>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ISearchTargets _searchTargets;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public DeleteWatermarkHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        ISearchTargets searchTargets,
        ChannelWriter<IBackgroundServiceRequest> workerChannel)
    {
        _dbContextFactory = dbContextFactory;
        _searchTargets = searchTargets;
        _workerChannel = workerChannel;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        DeleteWatermark request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, ChannelWatermark> validation = await WatermarkMustExist(
            dbContext,
            request,
            cancellationToken);
        return await validation.Apply(p => DoDeletion(dbContext, p, cancellationToken));
    }

    private async Task<Unit> DoDeletion(
        TvContext dbContext,
        ChannelWatermark watermark,
        CancellationToken cancellationToken)
    {
        List<string> channelNumbers = await NextPlayoutDependents.ForWatermark(
            dbContext,
            watermark.Id,
            cancellationToken);

        await dbContext.Database.ExecuteSqlAsync(
            $"UPDATE Channel SET WatermarkId = NULL WHERE WatermarkId = {watermark.Id}",
            cancellationToken);
        dbContext.ChannelWatermarks.Remove(watermark);
        await dbContext.SaveChangesAsync(cancellationToken);
        _searchTargets.SearchTargetsChanged();

        await _workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);

        return Unit.Default;
    }

    private static Task<Validation<BaseError, ChannelWatermark>> WatermarkMustExist(
        TvContext dbContext,
        DeleteWatermark request,
        CancellationToken cancellationToken) =>
        dbContext.ChannelWatermarks
            .SelectOneAsync(p => p.Id, p => p.Id == request.WatermarkId, cancellationToken)
            .Map(o => o.ToValidation<BaseError>($"Watermark {request.WatermarkId} does not exist"));
}

using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Watermarks;

public class UpdateWatermarkHandler : IRequestHandler<UpdateWatermark, Either<BaseError, UpdateWatermarkResult>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ISearchTargets _searchTargets;
    private readonly ChannelWriter<IBackgroundServiceRequest> _workerChannel;

    public UpdateWatermarkHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        ISearchTargets searchTargets,
        ChannelWriter<IBackgroundServiceRequest> workerChannel)
    {
        _dbContextFactory = dbContextFactory;
        _searchTargets = searchTargets;
        _workerChannel = workerChannel;
    }

    public async Task<Either<BaseError, UpdateWatermarkResult>> Handle(
        UpdateWatermark request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, ChannelWatermark> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(p => ApplyUpdateRequest(dbContext, p, request, cancellationToken));
    }

    private async Task<UpdateWatermarkResult> ApplyUpdateRequest(
        TvContext dbContext,
        ChannelWatermark p,
        UpdateWatermark update,
        CancellationToken cancellationToken)
    {
        p.Name = update.Name;

        p.Image = null;
        p.OriginalContentType = null;
        if (update.ImageSource == ChannelWatermarkImageSource.Custom)
        {
            p.Image = update.Image?.Path;
            p.OriginalContentType = update.Image?.ContentType;
        }

        p.Mode = update.Mode;
        p.ImageSource = update.ImageSource;
        p.Location = update.Location;
        p.Size = update.Size;
        p.WidthPercent = update.Width;
        p.HorizontalMarginPercent = update.HorizontalMargin;
        p.VerticalMarginPercent = update.VerticalMargin;
        p.FrequencyMinutes = update.FrequencyMinutes;
        p.DurationSeconds = update.DurationSeconds;
        p.Opacity = update.Opacity;
        p.PlaceWithinSourceContent = update.PlaceWithinSourceContent;
        p.OpacityExpression = update.Mode is ChannelWatermarkMode.OpacityExpression ? update.OpacityExpression : null;
        p.ZIndex = update.ZIndex;

        await dbContext.SaveChangesAsync(cancellationToken);
        _searchTargets.SearchTargetsChanged();

        List<string> channelNumbers = await NextPlayoutDependents.ForWatermark(dbContext, p.Id, cancellationToken);
        await _workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);

        return new UpdateWatermarkResult(p.Id);
    }

    private static async Task<Validation<BaseError, ChannelWatermark>> Validate(
        TvContext dbContext,
        UpdateWatermark request,
        CancellationToken cancellationToken) =>
        (await WatermarkMustExist(dbContext, request, cancellationToken), await ValidateName(dbContext, request))
        .Apply((watermark, _) => watermark);

    private static Task<Validation<BaseError, ChannelWatermark>> WatermarkMustExist(
        TvContext dbContext,
        UpdateWatermark updateWatermark,
        CancellationToken cancellationToken) =>
        dbContext.ChannelWatermarks
            .SelectOneAsync(p => p.Id, p => p.Id == updateWatermark.Id, cancellationToken)
            .Map(o => o.ToValidation<BaseError>("Watermark does not exist."));

    private static async Task<Validation<BaseError, string>> ValidateName(
        TvContext dbContext,
        UpdateWatermark updateWatermark)
    {
        bool duplicateName = await dbContext.ChannelWatermarks
            .AnyAsync(wm => wm.Id != updateWatermark.Id && wm.Name == updateWatermark.Name);

        Validation<BaseError, Unit> result2 = duplicateName
            ? Fail<BaseError, Unit>("ChannelWatermark name must be unique")
            : Success<BaseError, Unit>(Unit.Default);

        Validation<BaseError, string> result1 = updateWatermark.NotEmpty(c => c.Name)
            .Bind(_ => updateWatermark.NotLongerThan(50)(c => c.Name));

        return (result1, result2).Apply((_, _) => updateWatermark.Name);
    }
}

using System.Threading.Channels;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Scheduling;

public class UpdateDecoHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> workerChannel)
    : IRequestHandler<UpdateDeco, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(UpdateDeco request, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, Deco> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(ps => ApplyUpdateRequest(dbContext, ps, request, cancellationToken));
    }

    private async Task<Unit> ApplyUpdateRequest(
        TvContext dbContext,
        Deco existing,
        UpdateDeco request,
        CancellationToken cancellationToken)
    {
        existing.Name = request.Name;

        // watermark
        bool hasWatermark = request.WatermarkMode is (DecoMode.Override or DecoMode.Merge);
        existing.WatermarkMode = request.WatermarkMode;
        existing.UseWatermarkDuringFiller = hasWatermark && request.UseWatermarkDuringFiller;

        if (hasWatermark)
        {
            // this is different than schedule item/playout item because we have to merge watermark ids
            IEnumerable<int> toAdd =
                request.WatermarkIds.Where(id => existing.DecoWatermarks.All(wm => wm.WatermarkId != id));
            IEnumerable<DecoWatermark> toRemove =
                existing.DecoWatermarks.Where(wm => !request.WatermarkIds.Contains(wm.WatermarkId));
            existing.DecoWatermarks.RemoveAll(toRemove.Contains);
            foreach (int watermarkId in toAdd)
            {
                existing.DecoWatermarks.Add(
                    new DecoWatermark
                    {
                        DecoId = existing.Id,
                        WatermarkId = watermarkId
                    });
            }
        }
        else
        {
            existing.DecoWatermarks.Clear();
        }

        // graphics elements
        bool hasGraphicsElements = request.GraphicsElementsMode is (DecoMode.Override or DecoMode.Merge);
        existing.GraphicsElementsMode = request.GraphicsElementsMode;
        existing.UseGraphicsElementsDuringFiller = hasGraphicsElements && request.UseGraphicsElementsDuringFiller;

        if (hasGraphicsElements)
        {
            // this is different than schedule item/playout item because we have to merge graphics element ids
            IEnumerable<int> toAdd =
                request.GraphicsElementIds.Where(id => existing.DecoGraphicsElements.All(ge => ge.GraphicsElementId != id));
            IEnumerable<DecoGraphicsElement> toRemove =
                existing.DecoGraphicsElements.Where(ge => !request.GraphicsElementIds.Contains(ge.GraphicsElementId));
            existing.DecoGraphicsElements.RemoveAll(toRemove.Contains);
            foreach (int graphicsElementId in toAdd)
            {
                existing.DecoGraphicsElements.Add(
                    new DecoGraphicsElement
                    {
                        DecoId = existing.Id,
                        GraphicsElementId = graphicsElementId
                    });
            }
        }
        else
        {
            existing.DecoGraphicsElements.Clear();
        }

        // break content
        existing.BreakContentMode = request.BreakContentMode;
        var breakContentToAdd =
            request.BreakContent.Where(bc => existing.BreakContent.All(b => b.Id != bc.Id)).ToList();
        IEnumerable<DecoBreakContent> breakContentToRemove =
            existing.BreakContent.Where(bc => !request.BreakContent.Map(b => b.Id).Contains(bc.Id));
        var breakContentToUpdate = request.BreakContent.Except(breakContentToAdd).ToList();

        existing.BreakContent.RemoveAll(breakContentToRemove.Contains);

        foreach (var toUpdate in breakContentToUpdate)
        {
            foreach (var ex in Optional(existing.BreakContent.FirstOrDefault(b => b.Id == toUpdate.Id)))
            {
                ex.CollectionType = toUpdate.CollectionType;
                ex.CollectionId = toUpdate.CollectionId;
                ex.MultiCollectionId = toUpdate.MultiCollectionId;
                ex.SmartCollectionId = toUpdate.SmartCollectionId;
                ex.PlaylistId = toUpdate.PlaylistId;
                ex.Placement = toUpdate.Placement;
            }
        }

        foreach (var add in breakContentToAdd)
        {
            existing.BreakContent.Add(new DecoBreakContent
            {
                DecoId = existing.Id,
                CollectionType = add.CollectionType,
                CollectionId = add.CollectionId,
                MultiCollectionId = add.MultiCollectionId,
                SmartCollectionId = add.SmartCollectionId,
                PlaylistId = add.PlaylistId,
                Placement = add.Placement
            });
        }


        // default filler
        existing.DefaultFillerMode = request.DefaultFillerMode;
        existing.DefaultFillerCollectionType = request.DefaultFillerCollectionType;
        existing.DefaultFillerCollectionId = null;
        existing.DefaultFillerMediaItemId = null;
        existing.DefaultFillerMultiCollectionId = null;
        existing.DefaultFillerSmartCollectionId = null;
        if (request.DefaultFillerMode is DecoMode.Override)
        {
            switch (request.DefaultFillerCollectionType)
            {
                case CollectionType.Collection:
                    existing.DefaultFillerCollectionId = request.DefaultFillerCollectionId;
                    break;
                case CollectionType.MultiCollection:
                    existing.DefaultFillerMultiCollectionId = request.DefaultFillerMultiCollectionId;
                    break;
                case CollectionType.SmartCollection:
                    existing.DefaultFillerSmartCollectionId = request.DefaultFillerSmartCollectionId;
                    break;
                default:
                    existing.DefaultFillerMediaItemId = request.DefaultFillerMediaItemId;
                    break;
            }
        }

        existing.DefaultFillerTrimToFit = request.DefaultFillerTrimToFit;

        // dead air fallback
        existing.DeadAirFallbackMode = request.DeadAirFallbackMode;
        existing.DeadAirFallbackCollectionType = request.DeadAirFallbackCollectionType;
        existing.DeadAirFallbackCollectionId = null;
        existing.DeadAirFallbackMediaItemId = null;
        existing.DeadAirFallbackMultiCollectionId = null;
        existing.DeadAirFallbackSmartCollectionId = null;
        if (request.DeadAirFallbackMode is DecoMode.Override)
        {
            switch (request.DeadAirFallbackCollectionType)
            {
                case CollectionType.Collection:
                    existing.DeadAirFallbackCollectionId = request.DeadAirFallbackCollectionId;
                    break;
                case CollectionType.MultiCollection:
                    existing.DeadAirFallbackMultiCollectionId = request.DeadAirFallbackMultiCollectionId;
                    break;
                case CollectionType.SmartCollection:
                    existing.DeadAirFallbackSmartCollectionId = request.DeadAirFallbackSmartCollectionId;
                    break;
                default:
                    existing.DeadAirFallbackMediaItemId = request.DeadAirFallbackMediaItemId;
                    break;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        List<string> channelNumbers = await NextPlayoutDependents.ForDeco(dbContext, existing.Id, cancellationToken);
        await workerChannel.QueueSyncNextPlayouts(channelNumbers, cancellationToken);

        return Unit.Default;
    }

    private static async Task<Validation<BaseError, Deco>> Validate(
        TvContext dbContext,
        UpdateDeco request,
        CancellationToken cancellationToken) =>
        (await DecoMustExist(dbContext, request, cancellationToken), await ValidateDecoName(dbContext, request),
            ValidateBreakContent(request))
        .Apply((deco, _, _) => deco);

    private static Task<Validation<BaseError, Deco>> DecoMustExist(
        TvContext dbContext,
        UpdateDeco request,
        CancellationToken cancellationToken) =>
        dbContext.Decos
            .Include(d => d.BreakContent)
            .Include(d => d.DecoWatermarks)
            .Include(d => d.DecoGraphicsElements)
            .SelectOneAsync(d => d.Id, d => d.Id == request.DecoId, cancellationToken)
            .Map(o => o.ToValidation<BaseError>("Deco does not exist"));

    private static async Task<Validation<BaseError, string>> ValidateDecoName(
        TvContext dbContext,
        UpdateDeco request)
    {
        if (request.Name.Length > 50)
        {
            return BaseError.New($"Deco name \"{request.Name}\" is invalid");
        }

        bool duplicateName = await dbContext.Decos
            .AnyAsync(d => d.Id != request.DecoId && d.DecoGroupId == request.DecoGroupId && d.Name == request.Name);

        return duplicateName
            ? BaseError.New($"A deco named \"{request.Name}\" already exists in that deco group")
            : Success<BaseError, string>(request.Name);
    }

    private static Validation<BaseError, Unit> ValidateBreakContent(UpdateDeco request)
    {
        int startCount = request.BreakContent.Count(bc => bc.Placement is DecoBreakPlacement.BlockStart);
        if (startCount > 1)
        {
            return BaseError.New("Deco may only contain one [Block Start] break content");
        }

        int betweenCount = request.BreakContent.Count(bc => bc.Placement is DecoBreakPlacement.BetweenBlockItems);
        if (betweenCount > 1)
        {
            return BaseError.New("Deco may only contain one [Between Block Items] break content");
        }

        int chapterCount = request.BreakContent.Count(bc => bc.Placement is DecoBreakPlacement.ChapterMarkers);
        if (chapterCount > 1)
        {
            return BaseError.New("Deco may only contain one [At Chapter Markers] break content");
        }

        int finishCount = request.BreakContent.Count(bc => bc.Placement is DecoBreakPlacement.BlockFinish);
        if (finishCount > 1)
        {
            return BaseError.New("Deco may only contain one [Block Finish] break content");
        }

        foreach (var breakContent in request.BreakContent)
        {
            switch (breakContent.CollectionType)
            {
                case CollectionType.Collection:
                    if (breakContent.CollectionId is null)
                    {
                        return BaseError.New("Break content must have valid collection");
                    }

                    break;

                case CollectionType.MultiCollection:
                    if (breakContent.MultiCollectionId is null)
                    {
                        return BaseError.New("Break content must have valid multi collection");
                    }

                    break;

                case CollectionType.SmartCollection:
                    if (breakContent.SmartCollectionId is null)
                    {
                        return BaseError.New("Break content must have valid smart collection");
                    }

                    break;

                case CollectionType.TelevisionShow:
                case CollectionType.TelevisionSeason:
                case CollectionType.Artist:
                    if (breakContent.MediaItemId is null)
                    {
                        return BaseError.New("Break content must have valid media item");
                    }

                    break;

                case CollectionType.Playlist:
                    if (breakContent.PlaylistId is null)
                    {
                        return  BaseError.New("Break content must have valid playlist");
                    }

                    break;
            }
        }

        return Unit.Default;
    }
}

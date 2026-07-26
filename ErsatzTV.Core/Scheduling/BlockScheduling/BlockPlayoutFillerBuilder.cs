using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace ErsatzTV.Core.Scheduling.BlockScheduling;

public class BlockPlayoutFillerBuilder(
    IMediaCollectionRepository mediaCollectionRepository,
    ITelevisionRepository televisionRepository,
    IArtistRepository artistRepository,
    ILogger<BlockPlayoutFillerBuilder> logger) : IBlockPlayoutFillerBuilder
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    public async Task<PlayoutBuildResult> Build(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken)
    {
        var filteredExistingItems = referenceData.ExistingItems
            .Where(i => !result.ItemsToRemove.Contains(i.Id))
            .ToList();

        var allItems = result.AddedItems.ToList();
        var removeBefore = await result.RemoveBefore.IfNoneAsync(DateTimeOffset.MaxValue);

        // existing items that finish before this are deleted from the database by this build,
        // so they must not be treated as existing items here
        DateTimeOffset deletedBefore = await result.RemoveBefore
            .Map(rb => rb - referenceData.MaxPlayoutOffset)
            .IfNoneAsync(DateTimeOffset.MinValue);

        if (mode is PlayoutBuildMode.Reset)
        {
            // remove all playout items with type filler
            // except block items that are hidden from the guide (guide mode)
            foreach (PlayoutItem item in filteredExistingItems)
            {
                if (item.FinishOffset < removeBefore)
                {
                    continue;
                }

                if (item.FillerKind is FillerKind.None or FillerKind.GuideMode)
                {
                    allItems.Add(item);
                    continue;
                }

                BlockPlayoutChangeDetection.RemoveItemAndHistory(referenceData, item, result);
            }
        }
        else
        {
            allItems.AddRange(filteredExistingItems);
        }

        var filteredExistingHistory = referenceData.PlayoutHistory
            .Where(h => !result.HistoryToRemove.Contains(h.Id))
            .ToList();

        var collectionEnumerators = new Dictionary<CollectionKey, IMediaCollectionEnumerator>();

        var breakContentResult = await AddBreakContent(
            playout,
            referenceData,
            mode,
            collectionEnumerators,
            allItems,
            filteredExistingHistory,
            result.AddedHistory,
            result.RemoveBefore,
            cancellationToken);

        // merge break content result
        result.AddedItems.AddRange(breakContentResult.AddedItems);
        result.AddedHistory.AddRange(breakContentResult.AddedHistory);
        foreach (int id in breakContentResult.ItemsToRemove)
        {
            result.ItemsToRemove.Add(id);
        }
        foreach (int id in breakContentResult.HistoryToRemove)
        {
            result.HistoryToRemove.Add(id);
        }

        allItems = referenceData.ExistingItems
            .Where(i => !result.ItemsToRemove.Contains(i.Id))
            .Where(i => i.FinishOffset >= deletedBefore)
            .ToList();
        allItems.AddRange(result.AddedItems);

        result = await AddDefaultFiller(
            playout,
            referenceData,
            result,
            collectionEnumerators,
            allItems,
            filteredExistingHistory,
            cancellationToken);

        return result;
    }

    private async Task<PlayoutBuildResult> AddBreakContent(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildMode mode,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        IReadOnlyCollection<PlayoutItem> allItems,
        IReadOnlyCollection<PlayoutHistory> filteredExistingHistory,
        IReadOnlyCollection<PlayoutHistory> addedHistory,
        Option<DateTimeOffset> removeBefore,
        CancellationToken cancellationToken)
    {
        var result = PlayoutBuildResult.Empty;

        // TODO: support other modes
        if (mode is not PlayoutBuildMode.Reset)
        {
            return result;
        }

        var allHistory = filteredExistingHistory.Append(addedHistory).ToList();

        // guide group is template item id
        // they are reused over multiple days, so we only want to group consecutive items
        IEnumerable<IGrouping<int, PlayoutItem>> consecutiveBlocks = allItems
            .Where(i => i.FinishOffset > removeBefore.IfNone(SystemTime.MinValueUtc))
            .GroupConsecutiveBy(item => item.GuideGroup);
        foreach (IGrouping<int, PlayoutItem> blockGroup in consecutiveBlocks)
        {
            var itemsInBlock = blockGroup.ToList();

            // find all item and history pairs (to move together)
            var itemsAndHistory = new List<ItemAndHistory>();
            foreach (var item in itemsInBlock)
            {
                var history = allHistory.FirstOrDefault(h => h.When == item.Start);
                if (history is null)
                {
                    throw new InvalidOperationException($"Unable to locate history for playout item at {item.Start}");
                }
                itemsAndHistory.Add(new ItemAndHistory(item, history));
            }

            var head = itemsInBlock[0];
            DateTimeOffset blockStart = new DateTimeOffset(head.GuideStart!.Value, TimeSpan.Zero).ToLocalTime();
            DateTimeOffset blockFinish = new DateTimeOffset(head.GuideFinish!.Value, TimeSpan.Zero).ToLocalTime();
            TimeSpan blockDuration = blockFinish - blockStart;
            TimeSpan totalItemDuration = TimeSpan.FromTicks(itemsInBlock.Sum(i => (i.Finish - i.Start).Ticks));
            TimeSpan remaining = blockDuration - totalItemDuration;

            // find applicable deco
            foreach (Deco deco in GetDecoFor(referenceData, blockStart))
            {
                if (deco.BreakContent.Count == 0)
                {
                    continue;
                }

                // logger.LogDebug(
                //     "Block {Id} add break content from {Start} to {Finish} with {Remaining} remaining",
                //     blockGroup.Key,
                //     blockStart,
                //     blockFinish,
                //     remaining);

                foreach (var blockStartContent in deco.BreakContent.Where(bc =>
                             bc.Placement is DecoBreakPlacement.BlockStart))
                {
                    DateTimeOffset currentTime = blockStart;

                    var collectionKey = CollectionKey.ForBreakContent(blockStartContent);
                    string historyKey = HistoryDetails.ForBreakContent(blockStartContent);

                    var enumerator = await GetEnumerator(
                        collectionEnumerators,
                        collectionKey,
                        historyKey,
                        currentTime,
                        () => filteredExistingHistory.Append(addedHistory).Append(result.AddedHistory).ToList(),
                        playout.Seed,
                        deco.Id,
                        cancellationToken);

                    int count = enumerator switch
                    {
                        PlaylistEnumerator pe => pe.CountForFiller,
                        _ => enumerator.Count
                    };

                    if (count == 0)
                    {
                        break;
                    }

                    // TODO: support more than playlist enumerator
                    if (enumerator is PlaylistEnumerator playlistEnumerator)
                    {
                        List<ItemAndHistory> toInsert = [];

                        for (var i = 0; i < playlistEnumerator.CountForFiller; i++)
                        {
                            foreach (MediaItem mediaItem in enumerator.Current)
                            {
                                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                                var filler = new PlayoutItem
                                {
                                    PlayoutId = playout.Id,
                                    MediaItemId = mediaItem.Id,
                                    Start = blockStart.UtcDateTime,
                                    Finish = blockStart.UtcDateTime + itemDuration,
                                    InPoint = TimeSpan.Zero,
                                    OutPoint = itemDuration,

                                    // FillerKind.Fallback will loop and avoid hw accel, so don't use that
                                    FillerKind = FillerKind.DecoDefault,

                                    CollectionKey = JsonConvert.SerializeObject(collectionKey, JsonSettings),
                                    GuideStart = head.GuideStart,
                                    GuideFinish = head.GuideFinish,
                                    GuideGroup = head.GuideGroup
                                };

                                var nextHistory = new PlayoutHistory
                                {
                                    PlayoutId = playout.Id,
                                    PlaybackOrder = PlaybackOrder.None,
                                    Index = enumerator.State.Index,
                                    When = blockStart.UtcDateTime,
                                    Finish = filler.FinishOffset.UtcDateTime,
                                    Key = historyKey,
                                    Details = HistoryDetails.ForMediaItem(mediaItem)
                                };

                                if (itemDuration > remaining)
                                {
                                    logger.LogDebug(
                                        "Block start item {Item} with duration {Duration} is too long for block with remaining time {Time}",
                                        mediaItem.Id,
                                        itemDuration,
                                        remaining);
                                }
                                else
                                {
                                    result.AddedItems.Add(filler);
                                    result.AddedHistory.Add(nextHistory);

                                    toInsert.Add(new ItemAndHistory(filler, nextHistory));
                                    remaining -= itemDuration;
                                    playlistEnumerator.MoveNext(currentTime);
                                    currentTime += itemDuration;
                                }
                            }
                        }

                        itemsAndHistory.InsertRange(0, toInsert);
                    }
                }

                DateTimeOffset adjustedTime = blockStart;
                foreach ((PlayoutItem playoutItem, PlayoutHistory playoutHistory) in itemsAndHistory)
                {
                    bool changed = playoutItem.Start != adjustedTime;

                    TimeSpan duration = playoutItem.Finish - playoutItem.Start;

                    playoutItem.Start = adjustedTime.UtcDateTime;
                    playoutItem.Finish = (adjustedTime + duration).UtcDateTime;

                    playoutHistory.When = playoutItem.Start;
                    playoutHistory.Finish = playoutItem.Finish;

                    adjustedTime = playoutItem.FinishOffset;

                    if (changed && playoutHistory.Id > 0)
                    {
                        // change existing history
                        result.HistoryToRemove.Add(playoutHistory.Id);
                        result.AddedHistory.Add(playoutHistory.Clone());

                        // change existing item
                        result.ItemsToRemove.Add(playoutItem.Id);
                        result.AddedItems.Add(playoutItem.Clone());
                    }
                }
            }
        }

        return result;
    }

    private async Task<PlayoutBuildResult> AddDefaultFiller(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        List<PlayoutItem> allItems,
        List<PlayoutHistory> filteredExistingHistory,
        CancellationToken cancellationToken)
    {
        // find all unscheduled periods
        var queue = new Queue<PlayoutItem>(allItems.OrderBy(i => i.Start));
        while (queue.Count > 1)
        {
            PlayoutItem one = queue.Dequeue();
            PlayoutItem two = queue.Peek();

            DateTimeOffset start = one.FinishOffset;
            DateTimeOffset finish = two.StartOffset;

            if (start == finish)
            {
                continue;
            }

            // find applicable deco
            foreach (Deco deco in GetDecoFor(referenceData, start))
            {
                if (!HasDefaultFiller(deco))
                {
                    continue;
                }

                var collectionKey = CollectionKey.ForDecoDefaultFiller(deco);
                string historyKey = HistoryDetails.ForDefaultFiller(deco);

                var enumerator = await GetEnumerator(
                    collectionEnumerators,
                    collectionKey,
                    historyKey,
                    start,
                    () => filteredExistingHistory.Append(result.AddedHistory).ToList(),
                    playout.Seed,
                    deco.Id,
                    cancellationToken);

                // skip this deco if the collection has no items
                if (enumerator.Count == 0)
                {
                    continue;
                }

                DateTimeOffset current = start;
                var pastTime = false;
                while (current < finish && !cancellationToken.IsCancellationRequested)
                {
                    foreach (MediaItem mediaItem in enumerator.Current)
                    {
                        TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                        // without a duration this loop can never reach the end of the gap
                        if (itemDuration <= TimeSpan.Zero)
                        {
                            logger.LogWarning(
                                "Default filler item {Item} has no duration; will stop filling gap at {Time}",
                                mediaItem.Id,
                                current);

                            pastTime = true;
                            break;
                        }

                        // add filler from deco to unscheduled period
                        var filler = new PlayoutItem
                        {
                            PlayoutId = playout.Id,
                            MediaItemId = mediaItem.Id,
                            Start = current.UtcDateTime,
                            Finish = current.UtcDateTime + itemDuration,
                            InPoint = TimeSpan.Zero,
                            OutPoint = itemDuration,

                            // FillerKind.Fallback will loop and avoid hw accel, so don't use that
                            FillerKind = FillerKind.DecoDefault,

                            CollectionKey = JsonConvert.SerializeObject(collectionKey, JsonSettings),
                            GuideStart = one.GuideStart,
                            GuideFinish = one.GuideFinish,
                            GuideGroup = one.GuideGroup
                        };

                        if (filler.FinishOffset > finish)
                        {
                            if (deco.DefaultFillerTrimToFit)
                            {
                                filler.Finish = finish.UtcDateTime;
                                filler.OutPoint = filler.Finish - filler.Start;
                            }
                            else
                            {
                                pastTime = true;
                                break;
                            }
                        }

                        result.AddedItems.Add(filler);

                        // create a playout history record
                        var nextHistory = new PlayoutHistory
                        {
                            PlayoutId = playout.Id,
                            PlaybackOrder = PlaybackOrder.Shuffle,
                            Index = enumerator.State.Index,
                            When = current.UtcDateTime,
                            Finish = filler.FinishOffset.UtcDateTime,
                            Key = historyKey,
                            Details = HistoryDetails.ForMediaItem(mediaItem)
                        };

                        result.AddedHistory.Add(nextHistory);

                        current += itemDuration;
                        enumerator.MoveNext(filler.StartOffset);
                    }

                    if (pastTime)
                    {
                        break;
                    }
                }
            }
        }

        return result;
    }

    private async Task<IMediaCollectionEnumerator> GetEnumerator(
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        CollectionKey collectionKey,
        string historyKey,
        DateTimeOffset currentTime,
        Func<List<PlayoutHistory>> allHistory,
        int playoutSeed,
        int seedOffset,
        CancellationToken cancellationToken)
    {
        // load collection items from db on demand
        if (!collectionEnumerators.TryGetValue(collectionKey, out IMediaCollectionEnumerator enumerator))
        {
            if (collectionKey.CollectionType is CollectionType.Playlist)
            {
                enumerator = await BlockPlayoutEnumerator.PlaylistForFiller(
                    mediaCollectionRepository,
                    collectionKey.PlaylistId!.Value,
                    currentTime,
                    playoutSeed,
                    allHistory(),
                    seedOffset,
                    historyKey,
                    cancellationToken);
            }
            else
            {
                List<MediaItem> collectionItems = await MediaItemsForCollection.Collect(
                    mediaCollectionRepository,
                    televisionRepository,
                    artistRepository,
                    collectionKey,
                    cancellationToken);

                enumerator = BlockPlayoutEnumerator.Shuffle(
                    collectionItems,
                    currentTime,
                    playoutSeed,
                    allHistory(),
                    seedOffset,
                    historyKey);
            }

            int count = enumerator switch
            {
                PlaylistEnumerator pe => pe.CountForFiller,
                _ => enumerator.Count
            };

            if (count == 0)
            {
                logger.LogWarning(
                    "Block filler contains empty collection {@Key}; no filler will be scheduled",
                    collectionKey);
            }

            collectionEnumerators.Add(collectionKey, enumerator);
        }

        return enumerator;
    }

    private static Option<Deco> GetDecoFor(PlayoutReferenceData referenceData, DateTimeOffset start)
    {
        Option<PlayoutTemplate> maybeTemplate =
            AlternateScheduleSelector.GetScheduleForDate(referenceData.PlayoutTemplates, start);
        foreach (PlayoutTemplate template in maybeTemplate)
        {
            if (template.DecoTemplate is not null)
            {
                foreach (DecoTemplateItem decoTemplateItem in template.DecoTemplate.Items)
                {
                    if (decoTemplateItem.StartTime <= start.TimeOfDay && decoTemplateItem.EndTime == TimeSpan.Zero ||
                        decoTemplateItem.EndTime > start.TimeOfDay)
                    {
                        switch (decoTemplateItem.Deco.DefaultFillerMode)
                        {
                            case DecoMode.Inherit:
                                return referenceData.Deco;
                            case DecoMode.Override:
                                return decoTemplateItem.Deco;
                            case DecoMode.Disable:
                            default:
                                return Option<Deco>.None;
                        }
                    }
                }
            }
        }

        return referenceData.Deco;
    }

    private static bool HasDefaultFiller(Deco deco)
    {
        switch (deco.DefaultFillerCollectionType)
        {
            case CollectionType.Collection:
                return deco.DefaultFillerCollectionId.HasValue;
            case CollectionType.TelevisionShow:
                return deco.DefaultFillerMediaItemId.HasValue;
            case CollectionType.TelevisionSeason:
                return deco.DefaultFillerMediaItemId.HasValue;
            case CollectionType.Artist:
                return deco.DefaultFillerMediaItemId.HasValue;
            case CollectionType.MultiCollection:
                return deco.DefaultFillerMultiCollectionId.HasValue;
            case CollectionType.SmartCollection:
                return deco.DefaultFillerSmartCollectionId.HasValue;
            default:
                return false;
        }
    }

    private record ItemAndHistory(PlayoutItem PlayoutItem, PlayoutHistory History);
}

using System.Text.Json;
using System.Text.Json.Serialization;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Map = LanguageExt.Map;

namespace ErsatzTV.Core.Scheduling.BlockScheduling;

public class BlockPlayoutBuilder(
    IConfigElementRepository configElementRepository,
    IMediaCollectionRepository mediaCollectionRepository,
    ITelevisionRepository televisionRepository,
    IArtistRepository artistRepository,
    ICollectionEtag collectionEtag,
    ILogger<BlockPlayoutBuilder> logger)
    : IBlockPlayoutBuilder
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    protected virtual ILogger Logger => logger;

    public virtual async Task<Either<BaseError, PlayoutBuildResult>> Build(
        DateTimeOffset start,
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildMode mode,
        CancellationToken cancellationToken)
    {
        PlayoutBuildResult result = PlayoutBuildResult.Empty;

        logger.LogDebug(
            "Building block playout {PlayoutId} for channel {ChannelNumber} - {ChannelName}",
            playout.Id,
            referenceData.Channel.Number,
            referenceData.Channel.Name);

        List<PlaybackOrder> allowedPlaybackOrders =
        [
            PlaybackOrder.Chronological,
            PlaybackOrder.SeasonEpisode,
            PlaybackOrder.Shuffle,
            PlaybackOrder.Random,
            PlaybackOrder.RandomRotation
        ];

        int daysToBuild = await GetDaysToBuild(cancellationToken);

        // get blocks to schedule
        List<EffectiveBlock> blocksToSchedule = EffectiveBlock.GetEffectiveBlocks(
            referenceData.PlayoutTemplates,
            start,
            TimeZoneInfo.Local,
            daysToBuild);

        if (blocksToSchedule.Count == 0)
        {
            return result;
        }

        // always start at the beginning of the block
        start = blocksToSchedule.Min(b => b.Start);

        // get all collection items for the playout
        Map<CollectionKey, List<MediaItem>> collectionMediaItems =
            await GetCollectionMediaItems(blocksToSchedule, cancellationToken);
        if (collectionMediaItems.Values.All(v => v.Count == 0))
        {
            logger.LogWarning("There are no media items to schedule");
            return result;
        }

        Map<CollectionKey, string> collectionEtags = GetCollectionEtags(collectionMediaItems);

        Dictionary<PlayoutItem, BlockKey> itemBlockKeys =
            BlockPlayoutChangeDetection.GetPlayoutItemToBlockKeyMap(referenceData);

        // remove items without a block key (shouldn't happen often, just upgrades)
        foreach (PlayoutItem item in referenceData.ExistingItems.Where(i =>
                     i.FillerKind is not FillerKind.DecoDefault && !itemBlockKeys.ContainsKey(i)))
        {
            result.ItemsToRemove.Add(item.Id);
        }

        // remove old items
        // importantly, this should not remove their history
        result = result with { RemoveBefore = start };

        (List<EffectiveBlock> updatedEffectiveBlocks, List<PlayoutItem> playoutItemsToRemove) =
            BlockPlayoutChangeDetection.FindUpdatedItems(
                start,
                referenceData.ExistingItems,
                itemBlockKeys,
                blocksToSchedule,
                collectionEtags);

        foreach (PlayoutItem playoutItem in playoutItemsToRemove)
        {
            BlockPlayoutChangeDetection.RemoveItemAndHistory(referenceData, playoutItem, result);
        }

        var playoutItemsToRemoveIds = playoutItemsToRemove.Select(i => i.Id).ToHashSet();
        var baseItems = referenceData.ExistingItems.Where(i => !playoutItemsToRemoveIds.Contains(i.Id)).ToList();

        DateTimeOffset currentTime = start;
        if (updatedEffectiveBlocks.Count > 0)
        {
            currentTime = updatedEffectiveBlocks.Min(eb => eb.Start);
        }

        foreach (EffectiveBlock effectiveBlock in updatedEffectiveBlocks)
        {
            DateTimeOffset maxExistingFinish = baseItems
                .Where(i => i.Start < effectiveBlock.Start.UtcDateTime)
                .Select(i => i.FinishOffset)
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();

            if (currentTime < effectiveBlock.Start)
            {
                currentTime = effectiveBlock.Start;
            }

            if (currentTime < maxExistingFinish)
            {
                currentTime = maxExistingFinish;
            }

            if (currentTime > effectiveBlock.Start)
            {
                logger.LogDebug(
                    "Will schedule block {Block} with start {Start} at {ActualStart}",
                    effectiveBlock.Block.Name,
                    effectiveBlock.Start,
                    currentTime);
            }
            else
            {
                logger.LogDebug(
                    "Will schedule block {Block} at {Start}",
                    effectiveBlock.Block.Name,
                    effectiveBlock.Start);
            }

            DateTimeOffset blockFinish = effectiveBlock.Start.AddMinutes(effectiveBlock.Block.Minutes);

            foreach (BlockItem blockItem in effectiveBlock.Block.Items.OrderBy(i => i.Index))
            {
                // TODO: support other playback orders
                if (!allowedPlaybackOrders.Contains(blockItem.PlaybackOrder))
                {
                    continue;
                }

                if (currentTime >= blockFinish)
                {
                    logger.LogDebug(
                        "Current time {Time} for block {Block} is beyond block finish {Finish}; will stop with this block's items",
                        currentTime,
                        effectiveBlock.Block.Name,
                        blockFinish);

                    break;
                }

                // check for playout history for this collection
                string historyKey = HistoryDetails.KeyForBlockItem(blockItem);
                //logger.LogDebug("History key for block item {Item} is {Key}", blockItem.Id, historyKey);

                IMediaCollectionEnumerator enumerator = GetEnumerator(
                    playout,
                    referenceData,
                    result,
                    blockItem,
                    currentTime,
                    historyKey,
                    collectionMediaItems);

                if (enumerator.Count == 0)
                {
                    result.Warnings.BlockItemSkippedEmptyCollection++;
                    continue;
                }

                var pastTime = false;
                var done = false;

                while (!done && !pastTime && !cancellationToken.IsCancellationRequested)
                {
                    foreach (MediaItem mediaItem in enumerator.Current)
                    {
                        logger.LogDebug(
                            "current item: {Id} / {Title}",
                            mediaItem.Id,
                            PlayoutBuilder.DisplayTitle(mediaItem));

                        TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                        // item will never fit in block
                        var blockDuration = TimeSpan.FromMinutes(effectiveBlock.Block.Minutes);
                        if (effectiveBlock.Block.StopScheduling is BlockStopScheduling.BeforeDurationEnd &&
                            itemDuration > blockDuration)
                        {
                            // enumerator.MinimumDuration is none for images and remote streams, which
                            // would skip forever, so use the same duration source as the check above
                            TimeSpan minimumDuration = collectionMediaItems[CollectionKey.ForBlockItem(blockItem)]
                                .Map(i => i.GetDurationForPlayout())
                                .OrderBy(identity)
                                .Head();

                            if (minimumDuration > blockDuration)
                            {
                                Logger.LogError(
                                    "Collection with minimum duration {Duration:hh\\:mm\\:ss} will never fit in block with duration {BlockDuration:hh\\:mm\\:ss}; skipping this block item!",
                                    minimumDuration,
                                    blockDuration);

                                done = true;
                                break;
                            }

                            Logger.LogWarning(
                                "Skipping playout item {Title} with duration {Duration:hh\\:mm\\:ss} that will never fit in block with duration {BlockDuration:hh\\:mm\\:ss}",
                                PlayoutBuilder.DisplayTitle(mediaItem),
                                itemDuration,
                                blockDuration);

                            enumerator.MoveNext(Option<DateTimeOffset>.None);
                            continue;
                        }

                        var collectionKey = CollectionKey.ForBlockItem(blockItem);

                        // create a playout item
                        var playoutItem = new PlayoutItem
                        {
                            PlayoutId = playout.Id,
                            MediaItemId = mediaItem.Id,
                            Start = currentTime.UtcDateTime,
                            Finish = currentTime.UtcDateTime + itemDuration,
                            InPoint = TimeSpan.Zero,
                            OutPoint = itemDuration,
                            FillerKind = blockItem.IncludeInProgramGuide ? FillerKind.None : FillerKind.GuideMode,
                            DisableWatermarks = blockItem.DisableWatermarks,
                            //CustomTitle = scheduleItem.CustomTitle,
                            //WatermarkId = scheduleItem.WatermarkId,
                            //PreferredAudioLanguageCode = scheduleItem.PreferredAudioLanguageCode,
                            //PreferredAudioTitle = scheduleItem.PreferredAudioTitle,
                            //PreferredSubtitleLanguageCode = scheduleItem.PreferredSubtitleLanguageCode,
                            //SubtitleMode = scheduleItem.SubtitleMode
                            GuideGroup = effectiveBlock.TemplateItemId,
                            GuideStart = effectiveBlock.Start.UtcDateTime,
                            GuideFinish = blockFinish.UtcDateTime,
                            BlockKey = JsonConvert.SerializeObject(effectiveBlock.BlockKey),
                            CollectionKey = JsonConvert.SerializeObject(collectionKey, JsonSettings),
                            CollectionEtag = collectionEtags[collectionKey],
                            PlayoutItemWatermarks = [],
                            PlayoutItemGraphicsElements = [],
                            SchedulingContext = GetSchedulingContext(blockItem, enumerator)
                        };

                        foreach (BlockItemWatermark blockItemWatermark in blockItem.BlockItemWatermarks ?? [])
                        {
                            playoutItem.PlayoutItemWatermarks.Add(
                                new PlayoutItemWatermark
                                {
                                    PlayoutItem = playoutItem,
                                    WatermarkId = blockItemWatermark.WatermarkId
                                });
                        }

                        foreach (BlockItemGraphicsElement blockItemGraphicsElement in blockItem
                                     .BlockItemGraphicsElements ??
                                 [])
                        {
                            playoutItem.PlayoutItemGraphicsElements.Add(
                                new PlayoutItemGraphicsElement
                                {
                                    PlayoutItem = playoutItem,
                                    GraphicsElementId = blockItemGraphicsElement.GraphicsElementId
                                });
                        }

                        if (effectiveBlock.Block.StopScheduling is BlockStopScheduling.BeforeDurationEnd
                            && playoutItem.FinishOffset > blockFinish)
                        {
                            logger.LogDebug(
                                "Current time {Time} for block {Block} would go beyond block finish {Finish}; will not schedule more items",
                                currentTime,
                                effectiveBlock.Block.Name,
                                blockFinish);

                            pastTime = true;
                            break;
                        }

                        result.AddedItems.Add(playoutItem);

                        // create a playout history record
                        var nextHistory = new PlayoutHistory
                        {
                            PlayoutId = playout.Id,
                            BlockId = blockItem.BlockId,
                            PlaybackOrder = blockItem.PlaybackOrder,
                            Index = enumerator.State.Index,
                            When = currentTime.UtcDateTime,
                            Finish = playoutItem.FinishOffset.UtcDateTime,
                            Key = historyKey,
                            Details = HistoryDetails.ForMediaItem(mediaItem)
                        };

                        //logger.LogDebug("Adding history item: {When}: {History}", nextHistory.When, nextHistory.Details);
                        result.AddedHistory.Add(nextHistory);

                        currentTime += itemDuration;
                        enumerator.MoveNext(playoutItem.StartOffset);
                        done = true;
                    }
                }

                if (pastTime)
                {
                    break;
                }
            }
        }

        result = CleanUpHistory(referenceData, start, result);

        return result;
    }

    protected virtual async Task<int> GetDaysToBuild(CancellationToken cancellationToken) =>
        await configElementRepository
            .GetValue<int>(ConfigElementKey.PlayoutDaysToBuild, cancellationToken)
            .IfNoneAsync(2);

    protected virtual IMediaCollectionEnumerator GetEnumerator(
        Playout playout,
        PlayoutReferenceData referenceData,
        PlayoutBuildResult result,
        BlockItem blockItem,
        DateTimeOffset currentTime,
        string historyKey,
        Map<CollectionKey, List<MediaItem>> collectionMediaItems)
    {
        var collectionKey = CollectionKey.ForBlockItem(blockItem);
        List<MediaItem> collectionItems = collectionMediaItems[collectionKey];

        // get enumerator
        IMediaCollectionEnumerator enumerator = blockItem.PlaybackOrder switch
        {
            PlaybackOrder.Chronological => BlockPlayoutEnumerator.Chronological(
                collectionItems,
                currentTime,
                referenceData.PlayoutHistory.Append(result.AddedHistory).ToList(),
                blockItem,
                historyKey,
                logger),
            PlaybackOrder.SeasonEpisode => BlockPlayoutEnumerator.SeasonEpisode(
                collectionItems,
                currentTime,
                referenceData.PlayoutHistory.Append(result.AddedHistory).ToList(),
                blockItem,
                historyKey,
                logger),
            PlaybackOrder.Shuffle => BlockPlayoutEnumerator.Shuffle(
                collectionItems,
                currentTime,
                playout.Seed,
                referenceData.PlayoutHistory.Append(result.AddedHistory).ToList(),
                blockItem,
                historyKey),
            PlaybackOrder.RandomRotation => BlockPlayoutEnumerator.RandomRotation(
                collectionItems,
                currentTime,
                playout.Seed,
                referenceData.PlayoutHistory.Append(result.AddedHistory).ToList(),
                blockItem,
                historyKey),
            _ => new RandomizedMediaCollectionEnumerator(
                collectionItems,
                new CollectionEnumeratorState { Seed = new Random().Next(), Index = 0 })
        };

        return enumerator;
    }

    private static PlayoutBuildResult CleanUpHistory(
        PlayoutReferenceData referenceData,
        DateTimeOffset start,
        PlayoutBuildResult result)
    {
        IEnumerable<PlayoutHistory> allItemsToDelete = referenceData.PlayoutHistory
            .GroupBy(h => h.Key)
            .SelectMany(group => group
                .Filter(h => h.Finish < start.UtcDateTime - referenceData.MaxPlayoutOffset)
                .OrderByDescending(h => h.Finish)
                .Tail());

        foreach (PlayoutHistory delete in allItemsToDelete)
        {
            result.HistoryToRemove.Add(delete.Id);
        }

        return result;
    }

    private async Task<Map<CollectionKey, List<MediaItem>>> GetCollectionMediaItems(
        List<EffectiveBlock> effectiveBlocks,
        CancellationToken cancellationToken)
    {
        var collectionKeys = effectiveBlocks.Map(b => b.Block.Items)
            .Flatten()
            .DistinctBy(i => i.Id)
            .Map(CollectionKey.ForBlockItem)
            .Distinct()
            .ToList();

        IEnumerable<Tuple<CollectionKey, List<MediaItem>>> tuples = await collectionKeys.Map(async collectionKey =>
            Tuple(
                collectionKey,
                await MediaItemsForCollection.Collect(
                    mediaCollectionRepository,
                    televisionRepository,
                    artistRepository,
                    collectionKey,
                    cancellationToken))).SequenceParallel();

        return Map.createRange(tuples);
    }

    private Map<CollectionKey, string> GetCollectionEtags(
        Map<CollectionKey, List<MediaItem>> collectionMediaItems)
    {
        var result = new Map<CollectionKey, string>();

        foreach ((CollectionKey key, List<MediaItem> items) in collectionMediaItems)
        {
            result = result.Add(key, collectionEtag.ForCollectionItems(items));
        }

        return result;
    }

    private static string GetSchedulingContext(BlockItem blockItem, IMediaCollectionEnumerator enumerator)
    {
        var context = new BlockSchedulingContext(
            blockItem.BlockId,
            blockItem.Id,
            enumerator.SchedulingContextName,
            enumerator.State.Seed,
            enumerator.State.Index);

        return System.Text.Json.JsonSerializer.Serialize(context, Options);
    }
}

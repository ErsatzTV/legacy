using System.Collections.Immutable;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using Newtonsoft.Json;
using Serilog;

namespace ErsatzTV.Core.Scheduling.BlockScheduling;

internal static class BlockPlayoutChangeDetection
{
    public static Dictionary<PlayoutItem, BlockKey> GetPlayoutItemToBlockKeyMap(PlayoutReferenceData referenceData)
    {
        var itemBlockKeys = new Dictionary<PlayoutItem, BlockKey>();
        foreach (PlayoutItem item in referenceData.ExistingItems)
        {
            if (!string.IsNullOrWhiteSpace(item.BlockKey))
            {
                BlockKey blockKey = JsonConvert.DeserializeObject<BlockKey>(item.BlockKey);
                itemBlockKeys.Add(item, blockKey);
            }
        }

        return itemBlockKeys.ToDictionary();
    }

    public static Tuple<List<EffectiveBlock>, List<PlayoutItem>> FindUpdatedItems(
        DateTimeOffset buildStart,
        List<PlayoutItem> playoutItems,
        Dictionary<PlayoutItem, BlockKey> itemBlockKeys,
        List<EffectiveBlock> blocksToSchedule,
        Map<CollectionKey, string> collectionEtags)
    {
        DateTimeOffset lastScheduledItem = playoutItems.Count == 0
            ? SystemTime.MinValueUtc
            : playoutItems.Max(i => i.StartOffset);

        var existingBlockKeys = itemBlockKeys.Values.ToImmutableHashSet();
        var blockKeysToSchedule = blocksToSchedule.Map(b => b.BlockKey).ToImmutableHashSet();
        var updatedBlocks = new System.Collections.Generic.HashSet<EffectiveBlock>();
        var updatedItemIds = new System.Collections.Generic.HashSet<int>();

        var earliestEffectiveBlocks = new Dictionary<BlockKey, DateTimeOffset>();
        var earliestBlocks = new Dictionary<int, DateTimeOffset>();

        // check for changed collections
        foreach (EffectiveBlock effectiveBlock in blocksToSchedule.OrderBy(b => b.Start))
        {
            foreach (PlayoutItem playoutItem in playoutItems)
            {
                if (!itemBlockKeys.TryGetValue(playoutItem, out BlockKey blockKey) ||
                    effectiveBlock.Block.Id != blockKey.b)
                {
                    continue;
                }

                bool isUpdated = string.IsNullOrWhiteSpace(playoutItem.CollectionKey);
                if (!isUpdated)
                {
                    CollectionKey collectionKey =
                        JsonConvert.DeserializeObject<CollectionKey>(playoutItem.CollectionKey);

                    // collection is no longer present or collection has been modified
                    isUpdated = !collectionEtags.ContainsKey(collectionKey) ||
                                collectionEtags[collectionKey] != playoutItem.CollectionEtag;
                }

                if (isUpdated)
                {
                    // playout item needs to be removed/re-added
                    updatedItemIds.Add(playoutItem.Id);

                    // block needs to be scheduled again
                    updatedBlocks.Add(effectiveBlock);

                    if (!earliestEffectiveBlocks.ContainsKey(effectiveBlock.BlockKey))
                    {
                        earliestEffectiveBlocks[effectiveBlock.BlockKey] = effectiveBlock.Start;
                    }

                    if (!earliestBlocks.ContainsKey(effectiveBlock.Block.Id))
                    {
                        earliestBlocks[effectiveBlock.Block.Id] = effectiveBlock.Start;
                    }
                }
            }
        }

        // process in sorted order to simplify checks
        foreach (EffectiveBlock effectiveBlock in blocksToSchedule.OrderBy(b => b.Start))
        {
            // future blocks always need to be scheduled
            if (effectiveBlock.Start > lastScheduledItem)
            {
                updatedBlocks.Add(effectiveBlock);
            }
            // if block key is not present in existingBlockKeys, the effective block is new or updated
            else if (!existingBlockKeys.Contains(effectiveBlock.BlockKey))
            {
                updatedBlocks.Add(effectiveBlock);

                if (!earliestEffectiveBlocks.ContainsKey(effectiveBlock.BlockKey))
                {
                    earliestEffectiveBlocks[effectiveBlock.BlockKey] = effectiveBlock.Start;
                }

                if (!earliestBlocks.ContainsKey(effectiveBlock.Block.Id))
                {
                    earliestBlocks[effectiveBlock.Block.Id] = effectiveBlock.Start;
                }
            }
            // if id is present, the block has been modified earlier, so this effective block also needs to update
            else if (earliestBlocks.ContainsKey(effectiveBlock.Block.Id))
            {
                updatedBlocks.Add(effectiveBlock);
            }
        }

        foreach ((BlockKey key, DateTimeOffset value) in earliestEffectiveBlocks)
        {
            Log.Logger.Debug("Earliest effective block: {Key} => {Value}", key, value);
        }

        foreach ((int blockId, DateTimeOffset value) in earliestBlocks)
        {
            Log.Logger.Debug("Earliest block id: {Id} => {Value}", blockId, value);
        }

        // find affected playout items (importantly, from within the effective window)
        foreach (PlayoutItem item in playoutItems.Where(pi => pi.StartOffset >= buildStart))
        {
            if (!itemBlockKeys.TryGetValue(item, out BlockKey blockKey))
            {
                continue;
            }

            bool blockKeyIsAffected = earliestEffectiveBlocks.TryGetValue(blockKey, out DateTimeOffset value) &&
                                      value <= item.StartOffset;

            bool blockIdIsAffected = earliestBlocks.TryGetValue(blockKey.b, out DateTimeOffset value2) &&
                                     value2 <= item.StartOffset;

            if (!blockKeysToSchedule.Contains(blockKey) || blockKeyIsAffected || blockIdIsAffected)
            {
                updatedItemIds.Add(item.Id);
            }
        }

        var itemsToRemove = playoutItems.Filter(i => updatedItemIds.Contains(i.Id)).ToList();
        return Tuple(updatedBlocks.ToList(), itemsToRemove);
    }

    public static void RemoveItemAndHistory(
        PlayoutReferenceData referenceData,
        PlayoutItem playoutItem,
        PlayoutBuildResult result)
    {
        result.ItemsToRemove.Add(playoutItem.Id);

        // earlier builds may have left more than one row at this time
        foreach (PlayoutHistory history in referenceData.PlayoutHistory.Where(h => h.When == playoutItem.Start))
        {
            result.HistoryToRemove.Add(history.Id);
        }
    }

    public static void RemoveOrphanedHistory(
        PlayoutReferenceData referenceData,
        DateTimeOffset start,
        PlayoutBuildResult result)
    {
        var itemStarts = referenceData.ExistingItems
            .Where(i => !result.ItemsToRemove.Contains(i.Id))
            .Select(i => i.Start)
            .ToHashSet();

        // orphaned history has no corresponding item
        foreach (PlayoutHistory history in referenceData.PlayoutHistory)
        {
            if (history.When >= start.UtcDateTime && !itemStarts.Contains(history.When))
            {
                result.HistoryToRemove.Add(history.Id);
            }
        }
    }
}

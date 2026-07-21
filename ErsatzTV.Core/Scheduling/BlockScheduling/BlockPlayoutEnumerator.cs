using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.BlockScheduling;

public static class BlockPlayoutEnumerator
{
    public static IMediaCollectionEnumerator Chronological(
        List<MediaItem> collectionItems,
        DateTimeOffset currentTime,
        List<PlayoutHistory> playoutHistory,
        BlockItem blockItem,
        string historyKey,
        ILogger logger)
    {
        DateTime historyTime = currentTime.UtcDateTime;
        Option<PlayoutHistory> maybeHistory = playoutHistory
            .Filter(h => h.BlockId == blockItem.BlockId)
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .OrderByDescending(h => h.When)
            .HeadOrNone();

        var state = new CollectionEnumeratorState { Seed = 0, Index = 0 };

        var enumerator = new ChronologicalMediaCollectionEnumerator(collectionItems, state);

        // seek to the appropriate place in the collection enumerator
        foreach (PlayoutHistory h in maybeHistory)
        {
            logger.LogDebug("History is applicable: {When}: {History}", h.When, h.Details);

            HistoryDetails.MoveToNextItem(
                collectionItems,
                h.Details,
                enumerator,
                blockItem.PlaybackOrder);
        }

        return enumerator;
    }

    public static IMediaCollectionEnumerator SeasonEpisode(
        List<MediaItem> collectionItems,
        DateTimeOffset currentTime,
        List<PlayoutHistory> playoutHistory,
        BlockItem blockItem,
        string historyKey,
        ILogger logger)
    {
        DateTime historyTime = currentTime.UtcDateTime;
        Option<PlayoutHistory> maybeHistory = playoutHistory
            .Filter(h => h.BlockId == blockItem.BlockId)
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .OrderByDescending(h => h.When)
            .HeadOrNone();

        var state = new CollectionEnumeratorState { Seed = 0, Index = 0 };

        var enumerator = new SeasonEpisodeMediaCollectionEnumerator(collectionItems, state);

        // seek to the appropriate place in the collection enumerator
        foreach (PlayoutHistory h in maybeHistory)
        {
            logger.LogDebug("History is applicable: {When}: {History}", h.When, h.Details);

            HistoryDetails.MoveToNextItem(
                collectionItems,
                h.Details,
                enumerator,
                blockItem.PlaybackOrder);
        }

        return enumerator;
    }

    public static IMediaCollectionEnumerator Shuffle(
        List<MediaItem> collectionItems,
        DateTimeOffset currentTime,
        int playoutSeed,
        List<PlayoutHistory> playoutHistory,
        BlockItem blockItem,
        string historyKey)
    {
        DateTime historyTime = currentTime.UtcDateTime;
        Option<PlayoutHistory> maybeHistory = playoutHistory
            .Filter(h => h.BlockId == blockItem.BlockId)
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .OrderByDescending(h => h.When)
            .HeadOrNone();

        var state = new CollectionEnumeratorState { Seed = playoutSeed + blockItem.BlockId, Index = 0 };
        foreach (PlayoutHistory h in maybeHistory)
        {
            state.Index = h.Index + 1;
        }

        // TODO: fix multi-collection groups, keep multi-part episodes together
        var mediaItems = collectionItems
            .Map(mi => new GroupedMediaItem(mi, null))
            .ToList();

        // it shouldn't matter which order the remaining items are shuffled in,
        // as long as already-played items are not included
        return new BlockPlayoutShuffledMediaCollectionEnumerator(mediaItems, state);
    }

    public static IMediaCollectionEnumerator Shuffle(
        List<MediaItem> collectionItems,
        DateTimeOffset currentTime,
        int playoutSeed,
        List<PlayoutHistory> playoutHistory,
        int seedOffset,
        string historyKey)
    {
        DateTime historyTime = currentTime.UtcDateTime;
        Option<PlayoutHistory> maybeHistory = playoutHistory
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .OrderByDescending(h => h.When)
            .HeadOrNone();

        var state = new CollectionEnumeratorState { Seed = playoutSeed + seedOffset, Index = 0 };
        foreach (PlayoutHistory h in maybeHistory)
        {
            state.Index = h.Index + 1;
        }

        // TODO: fix multi-collection groups, keep multi-part episodes together
        var mediaItems = collectionItems
            .Map(mi => new GroupedMediaItem(mi, null))
            .ToList();

        // it shouldn't matter which order the remaining items are shuffled in,
        // as long as already-played items are not included
        return new BlockPlayoutShuffledMediaCollectionEnumerator(mediaItems, state);
    }

    public static async Task<IMediaCollectionEnumerator> PlaylistForFiller(
        IMediaCollectionRepository mediaCollectionRepository,
        int playlistId,
        DateTimeOffset currentTime,
        int playoutSeed,
        IReadOnlyCollection<PlayoutHistory> playoutHistory,
        int seedOffset,
        string historyKey,
        CancellationToken cancellationToken)
    {
        Dictionary<PlaylistItem, List<MediaItem>> itemMap =
            await mediaCollectionRepository.GetPlaylistItemMap(playlistId, cancellationToken);

        var playlistMediaItems = new Dictionary<CollectionKey, List<MediaItem>>();
        foreach ((PlaylistItem playlistItem, List<MediaItem> mediaItems) in itemMap)
        {
            playlistMediaItems.TryAdd(CollectionKey.ForPlaylistItem(playlistItem), mediaItems);
        }

        var state = new CollectionEnumeratorState { Seed = playoutSeed + seedOffset, Index = 0 };

        var enumerator = await PlaylistEnumerator.Create(
            mediaCollectionRepository,
            itemMap,
            state,
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            cancellationToken);

        DateTime historyTime = currentTime.UtcDateTime;
        Option<DateTime> maxWhen = await playoutHistory
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .Map(h => h.When)
            .OrderByDescending(h => h)
            .HeadOrNone()
            .IfNoneAsync(DateTime.MinValue);

        var maybeHistory = playoutHistory
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When == maxWhen)
            .ToList();

        Option<PlayoutHistory> maybePrimaryHistory = maybeHistory
            .Filter(h => string.IsNullOrWhiteSpace(h.ChildKey))
            .HeadOrNone();

        foreach (PlayoutHistory primaryHistory in maybePrimaryHistory)
        {
            var hasSetEnumeratorIndex = false;

            var childEnumeratorKeys = enumerator.ChildEnumerators.Map(x => x.CollectionKey).ToList();
            foreach ((IMediaCollectionEnumerator childEnumerator, CollectionKey collectionKey) in
                     enumerator.ChildEnumerators)
            {
                PlaybackOrder itemPlaybackOrder = childEnumerator switch
                {
                    ChronologicalMediaCollectionEnumerator => PlaybackOrder.Chronological,
                    RandomizedMediaCollectionEnumerator => PlaybackOrder.Random,
                    ShuffledMediaCollectionEnumerator => PlaybackOrder.Shuffle,
                    _ => PlaybackOrder.None
                };

                Option<PlayoutHistory> maybeApplicableHistory = maybeHistory
                    .Filter(h => h.ChildKey == HistoryDetails.KeyForCollectionKey(collectionKey))
                    .HeadOrNone();

                List<MediaItem> collectionItems = playlistMediaItems[collectionKey];
                if (collectionItems.Count == 0)
                {
                    continue;
                }

                foreach (PlayoutHistory h in maybeApplicableHistory)
                {
                    // logger.LogDebug(
                    //     "History is applicable: {When}: {ChildKey} / {History} / {IsCurrentChild}",
                    //     h.When,
                    //     h.ChildKey,
                    //     h.Details,
                    //     h.IsCurrentChild);

                    enumerator.ResetState(
                        new CollectionEnumeratorState
                        {
                            Seed = enumerator.State.Seed,
                            Index = h.Index + (h.IsCurrentChild ? 1 : 0)
                        });

                    if (itemPlaybackOrder is PlaybackOrder.Chronological)
                    {
                        HistoryDetails.MoveToNextItem(
                            collectionItems,
                            h.Details,
                            childEnumerator,
                            itemPlaybackOrder,
                            true);
                    }

                    if (h.IsCurrentChild)
                    {
                        // try to find enumerator based on collection key
                        enumerator.SetEnumeratorIndex(childEnumeratorKeys.IndexOf(collectionKey));
                        hasSetEnumeratorIndex = true;
                    }
                }
            }

            if (!hasSetEnumeratorIndex)
            {
                // falling back to enumerator based on index
                enumerator.SetEnumeratorIndex(primaryHistory.Index);
            }

            // only move next at the end, because that may also move
            // the enumerator index
            enumerator.MoveNext(Option<DateTimeOffset>.None);
        }

        return enumerator;
    }

    public static IMediaCollectionEnumerator RandomRotation(
        List<MediaItem> collectionItems,
        DateTimeOffset currentTime,
        int playoutSeed,
        List<PlayoutHistory> playoutHistory,
        BlockItem blockItem,
        string historyKey)
    {
        DateTime historyTime = currentTime.UtcDateTime;
        Option<PlayoutHistory> maybeHistory = playoutHistory
            .Filter(h => h.BlockId == blockItem.BlockId)
            .Filter(h => h.Key == historyKey)
            .Filter(h => h.When < historyTime)
            .OrderByDescending(h => h.When)
            .HeadOrNone();

        var state = new CollectionEnumeratorState { Seed = playoutSeed + blockItem.BlockId, Index = 0 };
        foreach (PlayoutHistory h in maybeHistory)
        {
            // Make sure to only increase the index by 1 since we can only
            // guarantee the next one is a different show. h.Index comes from
            // the previous play item which already increased by 1.
            state.Index = h.Index;
        }

        return new RandomizedRotatingMediaCollectionEnumerator(collectionItems, state);
    }
}

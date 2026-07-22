using System.Collections.Immutable;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public class PlaylistEnumerator : IMediaCollectionEnumerator
{
    private readonly System.Collections.Generic.HashSet<int> _remainingMediaItemIds = [];
    private System.Collections.Generic.HashSet<int> _allMediaItemIds;
    private System.Collections.Generic.HashSet<int> _idsToIncludeInEPG;
    private CloneableRandom _random;
    private bool _shufflePlaylistItems;
    private List<EnumeratorPlayAllCount> _sortedEnumerators;
    private int _itemsTakenFromCurrent;
    private Option<int> _batchSize = Option<int>.None;

    private PlaylistEnumerator()
    {
    }

    public int CountForRandom => _allMediaItemIds.Count;

    public int CountForFiller => _sortedEnumerators.Select(t => t.PlayAll ? t.Enumerator.Count : t.Count ?? 1).Sum();

    public ImmutableList<PlaylistEnumeratorCollectionKey> ChildEnumerators { get; private set; }

    public bool CurrentEnumeratorPlayAll => _sortedEnumerators[EnumeratorIndex].PlayAll;

    public int EnumeratorIndex { get; private set; }

    public void ResetState(CollectionEnumeratorState state)
    {
        // seed doesn't matter here
        State.Index = state.Index;
        State.Started = state.Started;
    }

    public string SchedulingContextName => "Playlist";

    public CollectionEnumeratorState State { get; private set; }

    public Option<MediaItem> Current => _sortedEnumerators.Count > 0
        ? _sortedEnumerators[EnumeratorIndex].Enumerator.Current
        : Option<MediaItem>.None;

    public Option<bool> CurrentIncludeInProgramGuide
    {
        get
        {
            foreach (MediaItem mediaItem in Current)
            {
                return _idsToIncludeInEPG.Contains(mediaItem.Id);
            }

            return Option<bool>.None;
        }
    }

    public int Count => throw new NotSupportedException("Count isn't used for playlist enumeration");

    public Option<TimeSpan> MinimumDuration { get; private set; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        foreach (MediaItem maybeMediaItem in _sortedEnumerators[EnumeratorIndex].Enumerator.Current)
        {
            _remainingMediaItemIds.Remove(maybeMediaItem.Id);
        }

        _sortedEnumerators[EnumeratorIndex].Enumerator.MoveNext(scheduledAt);
        _itemsTakenFromCurrent++;

        bool shouldSwitchEnumerator = _batchSize.Match(
            // move to the next enumerator if we've hit the batch size
            batchSize => _itemsTakenFromCurrent >= batchSize,
            () =>
            {
                // if we just finished playing all, move to the next enumerator
                if (_sortedEnumerators[EnumeratorIndex].PlayAll)
                {
                    return _sortedEnumerators[EnumeratorIndex].Enumerator.State.Index == 0;
                }

                // if we have played the desired count, move to the next enumerator
                if (_sortedEnumerators[EnumeratorIndex].Count is { } count)
                {
                    return _itemsTakenFromCurrent >= count;
                }

                // otherwise, always move
                return true;
            });

        if (shouldSwitchEnumerator)
        {
            EnumeratorIndex = (EnumeratorIndex + 1) % _sortedEnumerators.Count;
            _itemsTakenFromCurrent = 0;
        }

        State.Index += 1;
        if (_remainingMediaItemIds.Count == 0 && EnumeratorIndex == 0 &&
            _sortedEnumerators[0].Enumerator.State.Index == 0)
        {
            State.Index = 0;
            _remainingMediaItemIds.UnionWith(_allMediaItemIds);

            if (_shufflePlaylistItems)
            {
                State.Seed = _random.Next();
                _random = new CloneableRandom(State.Seed);
                _sortedEnumerators = ShufflePlaylistItems();
            }
        }

        State.Started = true;
    }

    public void SetEnumeratorIndex(int enumeratorIndex) => EnumeratorIndex = enumeratorIndex % _sortedEnumerators.Count;

    public static async Task<PlaylistEnumerator> Create(
        IMediaCollectionRepository mediaCollectionRepository,
        Dictionary<PlaylistItem, List<MediaItem>> playlistItemMap,
        CollectionEnumeratorState state,
        bool shufflePlaylistItems,
        Option<int> batchSize,
        bool randomStartPoint,
        CancellationToken cancellationToken)
    {
        var result = new PlaylistEnumerator
        {
            _sortedEnumerators = [],
            _idsToIncludeInEPG = [],
            _shufflePlaylistItems = shufflePlaylistItems,
            _batchSize = batchSize
        };

        // random start points only apply to a fresh build with no saved state
        var random = new Random(state.Seed);
        randomStartPoint = randomStartPoint && !state.Started;

        // collections should share enumerators
        var enumeratorMap = new Dictionary<CollectionKey, IMediaCollectionEnumerator>();
        result._allMediaItemIds = [];

        foreach (PlaylistItem playlistItem in playlistItemMap.Keys.OrderBy(i => i.Index))
        {
            List<MediaItem> items = playlistItemMap[playlistItem];
            foreach (MediaItem mediaItem in items)
            {
                result._allMediaItemIds.Add(mediaItem.Id);

                if (playlistItem.IncludeInProgramGuide)
                {
                    result._idsToIncludeInEPG.Add(mediaItem.Id);
                }
            }

            var collectionKey = CollectionKey.ForPlaylistItem(playlistItem);
            if (enumeratorMap.TryGetValue(collectionKey, out IMediaCollectionEnumerator enumerator))
            {
                result._sortedEnumerators.Add(
                    new EnumeratorPlayAllCount(enumerator, playlistItem.PlayAll, playlistItem.Count));
                continue;
            }

            var initState = new CollectionEnumeratorState { Seed = state.Seed, Index = 0 };
            if (items.Count == 1)
            {
                enumerator = new SingleMediaItemEnumerator(items.Head());
            }
            else
            {
                switch (playlistItem.PlaybackOrder)
                {
                    case PlaybackOrder.Chronological:
                        if (randomStartPoint)
                        {
                            initState.Index = items.Count > 0 ? random.Next(0, items.Count) : 0;
                        }

                        enumerator = new ChronologicalMediaCollectionEnumerator(items, initState);
                        break;
                    // TODO: fix multi episode shuffle?
                    case PlaybackOrder.MultiEpisodeShuffle:
                    case PlaybackOrder.Shuffle:
                        List<GroupedMediaItem> i = await PlayoutBuilder.GetGroupedMediaItemsForShuffle(
                            mediaCollectionRepository,
                            // TODO: fix this
                            new ProgramSchedule { KeepMultiPartEpisodesTogether = false },
                            items,
                            CollectionKey.ForPlaylistItem(playlistItem),
                            cancellationToken);
                        enumerator = new ShuffledMediaCollectionEnumerator(i, initState, cancellationToken);
                        break;
                    case PlaybackOrder.ShuffleInOrder:
                        enumerator = new ShuffleInOrderCollectionEnumerator(
                            await PlayoutBuilder.GetCollectionItemsForShuffleInOrder(
                                mediaCollectionRepository,
                                CollectionKey.ForPlaylistItem(playlistItem),
                                cancellationToken),
                            initState,
                            randomStartPoint,
                            cancellationToken);
                        break;
                    case PlaybackOrder.SeasonEpisode:
                        if (randomStartPoint)
                        {
                            initState.Index = items.Count > 0 ? random.Next(0, items.Count) : 0;
                        }

                        enumerator = new SeasonEpisodeMediaCollectionEnumerator(items, initState);
                        // season, episode will filter out season 0, so we may get an empty enumerator back
                        if (enumerator.Count == 0)
                        {
                            enumerator = null;
                        }

                        break;
                    case PlaybackOrder.Random:
                        enumerator = new RandomizedMediaCollectionEnumerator(items, initState);
                        break;
                }
            }

            if (enumerator is not null)
            {
                enumeratorMap.Add(collectionKey, enumerator);
                result._sortedEnumerators.Add(
                    new EnumeratorPlayAllCount(enumerator, playlistItem.PlayAll, playlistItem.Count));
            }
        }

        result._remainingMediaItemIds.UnionWith(result._allMediaItemIds);

        result.MinimumDuration = playlistItemMap.Values
            .Flatten()
            .Bind(i => i.GetNonZeroDuration())
            .OrderBy(identity)
            .HeadOrNone();

        result._random = new CloneableRandom(state.Seed);

        if (shufflePlaylistItems)
        {
            result._sortedEnumerators = result.ShufflePlaylistItems();
        }

        result.State = new CollectionEnumeratorState { Seed = state.Seed, Started = state.Started };
        result.EnumeratorIndex = 0;

        // this was a bug when playlist enumerators were first added; shouldn't happen anymore
        if (state.Index < 0)
        {
            state.Index = 0;
        }

        while (result.State.Index < state.Index)
        {
            result.MoveNext(Option<DateTimeOffset>.None);

            // previous state is no longer valid; playlist now has fewer items
            if (result.State.Index == 0)
            {
                break;
            }
        }

        var childEnumerators = new List<PlaylistEnumeratorCollectionKey>();
        foreach ((IMediaCollectionEnumerator enumerator, _, _) in result._sortedEnumerators)
        {
            foreach ((CollectionKey collectionKey, _) in enumeratorMap.Find(e => e.Value == enumerator))
            {
                childEnumerators.Add(new PlaylistEnumeratorCollectionKey(enumerator, collectionKey));
            }
        }

        result.ChildEnumerators = childEnumerators.ToImmutableList();

        return result;
    }

    private List<EnumeratorPlayAllCount> ShufflePlaylistItems()
    {
        if (_sortedEnumerators.Count < 3)
        {
            return _sortedEnumerators;
        }

        EnumeratorPlayAllCount[] copy = _sortedEnumerators.ToArray();
        EnumeratorPlayAllCount last = _sortedEnumerators.Last();

        do
        {
            int n = copy.Length;
            while (n > 1)
            {
                n--;
                int k = _random.Next(n + 1);
                (copy[k], copy[n]) = (copy[n], copy[k]);
            }
        } while (copy.First() == last);

        return copy.ToList();
    }

    private record EnumeratorPlayAllCount(IMediaCollectionEnumerator Enumerator, bool PlayAll, int? Count);
}

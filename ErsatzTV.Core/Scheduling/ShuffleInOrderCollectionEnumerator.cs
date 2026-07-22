using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public class ShuffleInOrderCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly CancellationToken _cancellationToken;
    private readonly IList<CollectionWithItems> _collections;
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;
    private readonly int _mediaItemCount;
    private readonly bool _randomStartPoint;
    private Random _random;
    private MediaItem[] _shuffled;

    public ShuffleInOrderCollectionEnumerator(
        IList<CollectionWithItems> collections,
        CollectionEnumeratorState state,
        bool randomStartPoint,
        CancellationToken cancellationToken)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        _collections = collections;
        _randomStartPoint = randomStartPoint;
        _cancellationToken = cancellationToken;
        _mediaItemCount = collections.Sum(c => c.MediaItems.Count);

        if (state.Index >= _mediaItemCount)
        {
            state.Index = 0;
            state.Seed = new Random(state.Seed).Next();
        }

        _random = new Random(state.Seed);
        _shuffled = Shuffle(_collections, _random);
        _lazyMinimumDuration =
            new Lazy<Option<TimeSpan>>(() =>
                _shuffled.Bind(i => i.GetNonZeroDuration()).OrderBy(identity).HeadOrNone());

        State = new CollectionEnumeratorState { Seed = state.Seed, Started = state.Started };
        while (State.Index < state.Index)
        {
            MoveNext(Option<DateTimeOffset>.None);
        }
    }

    public void ResetState(CollectionEnumeratorState state)
    {
        // only re-shuffle if needed
        if (State.Seed != state.Seed)
        {
            _random = new Random(state.Seed);
            _shuffled = Shuffle(_collections, _random);
        }

        State.Seed = state.Seed;
        State.Index = state.Index;
        State.Started = state.Started;
    }

    public string SchedulingContextName => "Shuffle in Order";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _shuffled.Length != 0 ? _shuffled[State.Index % _mediaItemCount] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        if (_shuffled.Length == 0)
        {
            return;
        }

        if ((State.Index + 1) % _shuffled.Length == 0)
        {
            Option<MediaItem> tail = Current;

            State.Index = 0;
            do
            {
                State.Seed = _random.Next();
                _random = new Random(State.Seed);
                _shuffled = Shuffle(_collections, _random);
            } while (!_cancellationToken.IsCancellationRequested && _collections.Count > 1 &&
                     _shuffled.Length > 0 && Current.Map(x => x.Id) == tail.Map(x => x.Id));
        }
        else
        {
            State.Index++;
        }

        if (_shuffled.Length > 0)
        {
            State.Index %= _shuffled.Length;
        }

        State.Started = true;
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _shuffled.Length;

    private MediaItem[] Shuffle(IList<CollectionWithItems> collections, Random random)
    {
        // based on https://keyj.emphy.de/balanced-shuffle/

        var orderedCollections = collections
            .Filter(c => c.ScheduleAsGroup)
            .Map(c => new OrderedCollection { Index = 0, Items = OrderItems(c) })
            .ToList();

        if (collections.Any(c => !c.ScheduleAsGroup))
        {
            orderedCollections.Add(
                new OrderedCollection
                {
                    Index = 0,
                    Items = Shuffle(
                        collections.Filter(c => !c.ScheduleAsGroup).SelectMany(c => c.MediaItems.Map(Some)),
                        random)
                });
        }

        List<OrderedCollection> filled = Fill(orderedCollections, random);

        var result = new List<MediaItem>();
        for (var i = 0; i < filled[0].Items.Count; i++)
        {
            var batch = new List<Option<MediaItem>>();

            foreach (OrderedCollection collection in filled)
            {
                int index = (collection.Index + i) % collection.Items.Count;
                batch.Add(collection.Items[index]);
            }

            foreach (Option<MediaItem> maybeItem in Shuffle(batch, random))
            {
                result.AddRange(maybeItem);
            }
        }

        return result.ToArray();
    }

    private List<OrderedCollection> Fill(List<OrderedCollection> orderedCollections, Random random)
    {
        var result = new List<OrderedCollection>();
        int maxLength = orderedCollections.Max(c => c.Items.Count);

        foreach (OrderedCollection collection in orderedCollections)
        {
            var items = new Queue<Option<MediaItem>>(collection.Items);
            var spaces = new Queue<Option<MediaItem>>(
                Range(0, maxLength - collection.Items.Count).Map(_ => Option<MediaItem>.None).ToList());

            Queue<Option<MediaItem>> smaller = collection.Items.Count < maxLength - collection.Items.Count
                ? items
                : spaces;
            Queue<Option<MediaItem>> larger = collection.Items.Count < maxLength - collection.Items.Count
                ? spaces
                : items;

            var ordered = new List<Option<MediaItem>>();

            int k = smaller.Count;
            while (k > 0)
            {
                int n = maxLength - ordered.Count;

                // compute optimal length +/- 10%
                double optimalLength = n / (double)k + (random.NextDouble() - 0.5) / 5.0;
                int r = Math.Clamp((int)optimalLength, 1, maxLength - k + 1);
                ordered.Add(smaller.Dequeue());
                for (var i = 0; i < r - 1; i++)
                {
                    ordered.Add(larger.Dequeue());
                }

                k--;
            }

            if (smaller.Count != 0)
            {
                ordered.AddRange(smaller);
            }

            if (larger.Count != 0)
            {
                ordered.AddRange(larger);
            }

            var index = 0;
            if (_randomStartPoint)
            {
                index = random.Next(0, ordered.Count);
            }

            result.Add(new OrderedCollection { Index = index, Items = ordered });
        }

        return result;
    }

    private static Option<MediaItem>[] OrderItems(CollectionWithItems collectionWithItems)
    {
        if (collectionWithItems.UseCustomOrder)
        {
            return collectionWithItems.MediaItems.Map(Some).ToArray();
        }

        return collectionWithItems.MediaItems
            .OrderBy(identity, new ChronologicalMediaComparer())
            .Map(Some)
            .ToArray();
    }

    private static Option<MediaItem>[] Shuffle(IEnumerable<Option<MediaItem>> list, Random random)
    {
        Option<MediaItem>[] copy = list.ToArray();

        int n = copy.Length;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            (copy[k], copy[n]) = (copy[n], copy[k]);
        }

        return copy;
    }

    private class OrderedCollection
    {
        public int Index { get; set; }
        public IList<Option<MediaItem>> Items { get; set; }
    }
}

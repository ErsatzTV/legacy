using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public class RandomizedMediaCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;
    private readonly IList<MediaItem> _mediaItems;
    private readonly Random _random;
    private int _index;

    public RandomizedMediaCollectionEnumerator(IList<MediaItem> mediaItems, CollectionEnumeratorState state)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        _mediaItems = mediaItems;
        _lazyMinimumDuration =
            new Lazy<Option<TimeSpan>>(() =>
                _mediaItems.Bind(i => i.GetNonZeroDuration()).OrderBy(identity).HeadOrNone());
        _random = new Random(state.Seed);

        State = new CollectionEnumeratorState { Seed = state.Seed, Started = state.Started };
        // we want to move at least once so we start with a random item and not the first
        // because _index defaults to 0
        if (State.Index == state.Index)
        {
            MoveNext(Option<DateTimeOffset>.None);
        }
        else
        {
            while (State.Index <= state.Index)
            {
                MoveNext(Option<DateTimeOffset>.None);
            }
        }
    }

    public void ResetState(CollectionEnumeratorState state)
    {
        // seed never changes here, no need to reset
        State.Index = state.Index;
        State.Started = state.Started;
    }

    public string SchedulingContextName => "Random";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _mediaItems.Any() ? _mediaItems[_index] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        if (_mediaItems.Count == 0)
        {
            return;
        }

        _index = _random.Next() % _mediaItems.Count;
        State.Index++;
        State.Started = true;
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _mediaItems.Count;
}

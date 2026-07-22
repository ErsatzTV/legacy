using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public class CustomOrderCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;

    private readonly List<MediaItem> _sortedMediaItems;

    public CustomOrderCollectionEnumerator(
        Collection collection,
        IList<MediaItem> mediaItems,
        CollectionEnumeratorState state)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        // TODO: this will break if we allow shows and seasons
        _sortedMediaItems = collection.CollectionItems
            .OrderBy(ci => ci.CustomIndex)
            .Map(ci => mediaItems.First(mi => mi.Id == ci.MediaItemId))
            .ToList();
        _lazyMinimumDuration = new Lazy<Option<TimeSpan>>(() =>
            _sortedMediaItems.Bind(i => i.GetNonZeroDuration()).OrderBy(identity).HeadOrNone());

        State = new CollectionEnumeratorState { Seed = state.Seed, Started = state.Started };
        while (State.Index < state.Index)
        {
            MoveNext(Option<DateTimeOffset>.None);
        }
    }

    public void ResetState(CollectionEnumeratorState state)
    {
        // seed doesn't matter here
        State.Index = state.Index;
        State.Started = state.Started;
    }

    public string SchedulingContextName => "Custom Order";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _sortedMediaItems.Count != 0 ? _sortedMediaItems[State.Index] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        State.Index = (State.Index + 1) % _sortedMediaItems.Count;
        State.Started = true;
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _sortedMediaItems.Count;
}

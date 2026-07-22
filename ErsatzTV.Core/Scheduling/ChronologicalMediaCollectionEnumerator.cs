using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public sealed class ChronologicalMediaCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly Lazy<Dictionary<int, int>> _lazyMediaItemGroupSize;
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;
    private readonly List<MediaItem> _sortedMediaItems;

    public ChronologicalMediaCollectionEnumerator(
        IEnumerable<MediaItem> mediaItems,
        CollectionEnumeratorState state)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        _sortedMediaItems = mediaItems.OrderBy(identity, new ChronologicalMediaComparer()).ToList();
        _lazyMinimumDuration = new Lazy<Option<TimeSpan>>(() =>
            _sortedMediaItems.Bind(i => i.GetNonZeroDuration()).OrderBy(identity).HeadOrNone());

        _lazyMediaItemGroupSize = new Lazy<Dictionary<int, int>>(CalculateMediaItemGroupSizes);

        State = new CollectionEnumeratorState { Seed = state.Seed, Started = state.Started };

        if (state.Index >= _sortedMediaItems.Count)
        {
            state.Index = 0;
            state.Seed = 0;
        }

        while (State.Index < state.Index)
        {
            MoveNext(Option<DateTimeOffset>.None);
        }
    }

    public void ResetState(CollectionEnumeratorState state)
    {
        // seed doesn't matter in chronological
        State.Index = state.Index;
        State.Started = state.Started;
    }

    public string SchedulingContextName => "Chronological";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _sortedMediaItems.Count != 0 ? _sortedMediaItems[State.Index] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        if (_sortedMediaItems.Count > 0)
        {
            State.Index = (State.Index + 1) % _sortedMediaItems.Count;
        }

        State.Started = true;
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _sortedMediaItems.Count;

    private Dictionary<int, int> CalculateMediaItemGroupSizes()
    {
        var result = new Dictionary<int, int>();

        List<GroupedMediaItem> groupedItems = MultiPartEpisodeGrouper.GroupMediaItems(_sortedMediaItems, false);
        foreach (GroupedMediaItem group in groupedItems)
        {
            int size = group.Additional.Count + 1;
            result[group.First.Id] = size;
            foreach (MediaItem additional in group.Additional)
            {
                result[additional.Id] = size;
            }
        }

        return result;
    }

    public int GroupSizeForMediaItem(MediaItem mediaItem) =>
        _lazyMediaItemGroupSize.Value.GetValueOrDefault(mediaItem.Id, 1);
}

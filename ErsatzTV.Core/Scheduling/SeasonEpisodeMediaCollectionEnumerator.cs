using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public sealed class SeasonEpisodeMediaCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;
    private readonly List<MediaItem> _sortedMediaItems;

    public SeasonEpisodeMediaCollectionEnumerator(
        IEnumerable<MediaItem> mediaItems,
        CollectionEnumeratorState state)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        _sortedMediaItems = mediaItems
            .Filter(mi => (mi is not Episode episode) || (episode.Season?.SeasonNumber ?? 0) > 0)
            .OrderBy(identity, new SeasonEpisodeMediaComparer()).ToList();
        _lazyMinimumDuration = new Lazy<Option<TimeSpan>>(() =>
            _sortedMediaItems.Bind(i => i.GetNonZeroDuration()).OrderBy(identity).HeadOrNone());

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
        // seed doesn't matter here
        State.Index = state.Index;
        State.Started = state.Started;
    }

    public string SchedulingContextName => "Season, Episode";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _sortedMediaItems.Count != 0 ? _sortedMediaItems[State.Index] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        if (_sortedMediaItems.Count == 0)
        {
            return;
        }

        State.Index = (State.Index + 1) % _sortedMediaItems.Count;
        State.Started = true;
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _sortedMediaItems.Count;
}

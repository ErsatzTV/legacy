using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling.BlockScheduling;

public class BlockPlayoutShuffledMediaCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;
    private readonly int _mediaItemCount;
    private readonly IList<GroupedMediaItem> _mediaItems;
    private IList<MediaItem> _shuffled;

    public BlockPlayoutShuffledMediaCollectionEnumerator(
        IList<GroupedMediaItem> mediaItems,
        CollectionEnumeratorState state)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        _mediaItems = mediaItems;
        _mediaItemCount = _mediaItems.Sum(i => 1 + Optional(i.Additional).Flatten().Count());

        State = state;

        _shuffled = Shuffle(_mediaItems);
        _lazyMinimumDuration =
            new Lazy<Option<TimeSpan>>(() =>
                _shuffled.Bind(i => i.GetNonZeroDuration()).OrderBy(identity).HeadOrNone());
    }

    public void ResetState(CollectionEnumeratorState state)
    {
        // only re-shuffle if needed
        if (State.Seed != state.Seed || State.Index != state.Index)
        {
            State.Seed = state.Seed;
            State.Index = state.Index;
            State.Started = state.Started;

            _shuffled = Shuffle(_mediaItems);
        }
    }

    public string SchedulingContextName => "Block Shuffle";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _shuffled.Any() ? _shuffled[State.Index % _mediaItemCount] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        State.Index++;
        if (State.Index % _mediaItemCount == 0)
        {
            _shuffled = Shuffle(_mediaItems);
        }

        State.Started = true;
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _shuffled.Count;

    private IList<MediaItem> Shuffle(IList<GroupedMediaItem> list)
    {
        var copy = new GroupedMediaItem[list.Count];

        var superShuffle = new SuperShuffle();
        for (var i = 0; i < list.Count; i++)
        {
            int toSelect = superShuffle.Shuffle(i, State.Seed + State.Index / list.Count, list.Count);
            copy[i] = list[toSelect];
        }

        return GroupedMediaItem.FlattenGroups(copy, _mediaItemCount);
    }
}

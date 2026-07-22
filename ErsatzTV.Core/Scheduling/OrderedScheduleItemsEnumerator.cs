using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public class OrderedScheduleItemsEnumerator : IScheduleItemsEnumerator
{
    private readonly List<ProgramScheduleItem> _sortedScheduleItems;

    public OrderedScheduleItemsEnumerator(
        IEnumerable<ProgramScheduleItem> scheduleItems,
        CollectionEnumeratorState state)
    {
        _sortedScheduleItems = scheduleItems.OrderBy(i => i.Index).ToList();

        State = new CollectionEnumeratorState { Seed = state.Seed, Started = state.Started };

        if (state.Index >= _sortedScheduleItems.Count)
        {
            state.Index = 0;
            state.Seed = 0;
        }

        while (State.Index < state.Index)
        {
            MoveNext();
        }
    }

    public CollectionEnumeratorState State { get; }

    public ProgramScheduleItem Current => _sortedScheduleItems[State.Index];

    public void MoveNext()
    {
        State.Index = (State.Index + 1) % _sortedScheduleItems.Count;
        State.Started = true;
    }

    public ProgramScheduleItem Peek(int offset) =>
        _sortedScheduleItems[(State.Index + offset) % _sortedScheduleItems.Count];
}

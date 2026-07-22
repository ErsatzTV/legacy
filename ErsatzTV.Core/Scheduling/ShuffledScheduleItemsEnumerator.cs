using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Scheduling;

namespace ErsatzTV.Core.Scheduling;

public class ShuffledScheduleItemsEnumerator : IScheduleItemsEnumerator
{
    private readonly IList<ProgramScheduleItem> _scheduleItems;
    private readonly int _scheduleItemsCount;
    private CloneableRandom _random;
    private ProgramScheduleItem[] _shuffled;

    public ShuffledScheduleItemsEnumerator(
        IList<ProgramScheduleItem> scheduleItems,
        CollectionEnumeratorState state)
    {
        _scheduleItemsCount = scheduleItems.Count;
        _scheduleItems = scheduleItems;

        if (state.Index >= _scheduleItems.Count)
        {
            state.Index = 0;
            state.Seed = new Random(state.Seed).Next();
        }

        _random = new CloneableRandom(state.Seed);
        _shuffled = Shuffle(_scheduleItems, _random);

        State = new CollectionEnumeratorState { Seed = state.Seed, Started = state.Started };
        while (State.Index < state.Index)
        {
            MoveNext();
        }
    }

    public CollectionEnumeratorState State { get; }

    public ProgramScheduleItem Current => _shuffled[State.Index % _scheduleItemsCount];

    public void MoveNext()
    {
        if ((State.Index + 1) % _scheduleItemsCount == 0)
        {
            ProgramScheduleItem tail = Current;

            State.Index = 0;
            do
            {
                State.Seed = _random.Next();
                _random = new CloneableRandom(State.Seed);
                _shuffled = Shuffle(_scheduleItems, _random);
            } while (_scheduleItems.Count > 1 && Current == tail);
        }
        else
        {
            State.Index++;
        }

        State.Index %= _scheduleItemsCount;
        State.Started = true;
    }

    public ProgramScheduleItem Peek(int offset)
    {
        if (offset == 0)
        {
            return Current;
        }

        if ((State.Index + offset) % _scheduleItemsCount == 0)
        {
            ProgramScheduleItem[] shuffled;
            ProgramScheduleItem tail = Current;

            // clone the random
            CloneableRandom randomCopy = _random.Clone();

            do
            {
                int newSeed = randomCopy.Next();
                randomCopy = new CloneableRandom(newSeed);
                shuffled = Shuffle(_scheduleItems, randomCopy);
            } while (_scheduleItems.Count > 1 && shuffled[0] == tail);

            return shuffled[0];
        }

        return _shuffled[(State.Index + offset) % _scheduleItemsCount];
    }

    private static ProgramScheduleItem[] Shuffle(IEnumerable<ProgramScheduleItem> list, CloneableRandom random)
    {
        ProgramScheduleItem[] copy = list.ToArray();

        int n = copy.Length;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            (copy[k], copy[n]) = (copy[n], copy[k]);
        }

        return copy;
    }
}

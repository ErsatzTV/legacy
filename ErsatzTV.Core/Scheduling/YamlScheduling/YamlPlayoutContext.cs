using System.Security.Cryptography;
using System.Text;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Newtonsoft.Json;

namespace ErsatzTV.Core.Scheduling.YamlScheduling;

public class YamlPlayoutContext(Playout playout, YamlPlayoutDefinition definition, int guideGroup)
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    private readonly System.Collections.Generic.HashSet<int> _channelWatermarkIds = [];
    private readonly Stack<FillerKind> _fillerKind = new();
    private readonly Dictionary<int, string> _graphicsElements = [];

    private System.Collections.Generic.HashSet<int> _visitedInstructions = [];
    private int _guideGroup = guideGroup;
    private bool _guideGroupLocked;
    private int _instructionIndex;
    private Option<MidRollSequence> _midRollSequence;
    private Option<string> _postRollSequence;
    private Option<string> _preRollSequence;

    // null active schedule => default playout
    private string _activeSchedule;
    private List<YamlPlayoutInstruction> _currentInstructions;

    // saved state for each playout list (default keyed by empty string) so switching
    // between schedules resumes each list's position and ambient modifiers cleanly
    private readonly Dictionary<string, string> _listFingerprints = [];
    private readonly Dictionary<string, ListState> _listStates = [];
    private readonly System.Collections.Generic.HashSet<string> _staleListStates = [];
    private Dictionary<string, string> _listFingerprintsToRestore;
    private Dictionary<string, List<SequenceOrder>> _sequenceOrdersToRestore;

    public Playout Playout { get; } = playout;

    public List<PlayoutItem> AddedItems { get; } = [];

    public List<PlayoutHistory> AddedHistory { get; } = [];

    public YamlPlayoutDefinition Definition { get; } = definition;

    public DateTimeOffset CurrentTime { get; set; }

    public int InstructionIndex
    {
        get => _instructionIndex;
        set
        {
            _instructionIndex = value;
            _visitedInstructions.Add(value);
        }
    }

    public bool VisitedAll => _visitedInstructions.Count >= CurrentInstructions.Count;

    // the instruction list currently being executed (default playout or an active schedule)
    public List<YamlPlayoutInstruction> CurrentInstructions => _currentInstructions ?? Definition.Playout;

    public string ActiveSchedule => _activeSchedule;

    public void RestoreSequenceOrders()
    {
        _listFingerprints.Clear();
        _staleListStates.Clear();
        foreach ((string listKey, List<YamlPlayoutInstruction> instructions) in GetInstructionLists())
        {
            string fingerprint = GetListFingerprint(instructions);
            _listFingerprints[listKey] = fingerprint;

            if (_listFingerprintsToRestore is not null &&
                (!_listFingerprintsToRestore.TryGetValue(listKey, out string savedFingerprint) ||
                 !string.Equals(savedFingerprint, fingerprint, StringComparison.Ordinal)))
            {
                _staleListStates.Add(listKey);
                ResetInstructionIndex(listKey);
            }
        }

        if (_sequenceOrdersToRestore is not null)
        {
            if (_listFingerprintsToRestore is null)
            {
                foreach (string listKey in _sequenceOrdersToRestore.Keys)
                {
                    _staleListStates.Add(listKey);
                    ResetInstructionIndex(listKey);
                }
            }

            foreach ((string listKey, List<YamlPlayoutInstruction> instructions) in GetInstructionLists())
            {
                RestoreSequenceOrders(listKey, instructions);
            }
        }

        _listFingerprintsToRestore = null;
        _sequenceOrdersToRestore = null;
    }

    // only return first instance of name; ignore unnamed schedules
    // this matches SwitchToSchedule (null is default, otherwise find first matching name)
    private IEnumerable<(string ListKey, List<YamlPlayoutInstruction> Instructions)> GetInstructionLists()
    {
        yield return (string.Empty, Definition.Playout);

        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (YamlPlayoutScheduleItem schedule in Definition.Schedules)
        {
            if (!string.IsNullOrWhiteSpace(schedule.Name) && seen.Add(schedule.Name))
            {
                yield return (schedule.Name, schedule.Playout);
            }
        }
    }

    private static string GetListFingerprint(List<YamlPlayoutInstruction> instructions)
    {
        var normalizedInstructions = instructions.ToList();
        foreach (SequenceGroup sequenceGroup in GetSequenceGroups(instructions, false))
        {
            List<YamlPlayoutInstruction> declarationOrder = sequenceGroup.Items
                .Select(x => x.Instruction)
                .OrderBy(i => i.SequenceIndex)
                .ToList();
            for (var index = 0; index < sequenceGroup.Items.Count; index++)
            {
                normalizedInstructions[sequenceGroup.Items[index].Index] = declarationOrder[index];
            }
        }

        IEnumerable<object> instructionState = normalizedInstructions.Select(instruction =>
            string.IsNullOrWhiteSpace(instruction.SequenceKey)
                ? new
                {
                    Kind = "instruction",
                    Instruction = JsonConvert.SerializeObject(instruction, Formatting.None)
                }
                : new
                {
                    Kind = "sequence",
                    Instruction = JsonConvert.SerializeObject(new
                    {
                        Type = instruction.GetType().FullName,
                        instruction.SequenceKey,
                        instruction.SequenceFingerprint,
                        instruction.SequenceIndex,
                        instruction.CustomTitle
                    }, Formatting.None)
                });
        string serializedState = JsonConvert.SerializeObject(instructionState, Formatting.None);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedState)));
    }

    private void RestoreSequenceOrders(string listKey, List<YamlPlayoutInstruction> instructions)
    {
        string normalizedListKey = listKey ?? string.Empty;
        if (_staleListStates.Contains(normalizedListKey) ||
            !_sequenceOrdersToRestore.TryGetValue(normalizedListKey, out List<SequenceOrder> savedOrders))
        {
            return;
        }

        if (savedOrders is null ||
            savedOrders.Count == 0 ||
            savedOrders.Any(o => o is null || string.IsNullOrWhiteSpace(o.Sequence) || o.Order is null))
        {
            ResetInstructionIndex(normalizedListKey);
            return;
        }

        Dictionary<string, Queue<SequenceGroup>> groupsBySequence = GetSequenceGroups(instructions, false)
            .GroupBy(g => g.Sequence)
            .ToDictionary(g => g.Key, g => new Queue<SequenceGroup>(g));
        var restorations = new List<(SequenceGroup Group, List<YamlPlayoutInstruction> Instructions)>();

        foreach (SequenceOrder savedOrder in savedOrders)
        {
            if (!groupsBySequence.TryGetValue(savedOrder.Sequence, out Queue<SequenceGroup> groups) ||
                !groups.TryDequeue(out SequenceGroup sequenceGroup) ||
                !TryRestoreSequenceOrder(savedOrder, sequenceGroup, out List<YamlPlayoutInstruction> restoredItems))
            {
                ResetInstructionIndex(normalizedListKey);
                return;
            }

            restorations.Add((sequenceGroup, restoredItems));
        }

        if (savedOrders.Select(o => o.Sequence).Distinct().Any(sequence => groupsBySequence[sequence].Count > 0))
        {
            ResetInstructionIndex(normalizedListKey);
            return;
        }

        foreach ((SequenceGroup sequenceGroup, List<YamlPlayoutInstruction> restoredItems) in restorations)
        {
            for (var index = 0; index < sequenceGroup.Items.Count; index++)
            {
                restoredItems[index].SequenceShuffled = true;
                instructions[sequenceGroup.Items[index].Index] = restoredItems[index];
            }
        }
    }

    private static bool TryRestoreSequenceOrder(
        SequenceOrder savedOrder,
        SequenceGroup sequenceGroup,
        out List<YamlPlayoutInstruction> restoredItems)
    {
        restoredItems = [];
        if (savedOrder.Order.Count != sequenceGroup.Items.Count)
        {
            return false;
        }

        Dictionary<int, Queue<YamlPlayoutInstruction>> instructionsByIndex = sequenceGroup.Items
            .GroupBy(x => x.Instruction.SequenceIndex)
            .ToDictionary(
                g => g.Key,
                g => new Queue<YamlPlayoutInstruction>(g.Select(x => x.Instruction)));

        foreach (int sequenceIndex in savedOrder.Order)
        {
            if (!instructionsByIndex.TryGetValue(sequenceIndex, out Queue<YamlPlayoutInstruction> candidates) ||
                !candidates.TryDequeue(out YamlPlayoutInstruction restoredInstruction))
            {
                restoredItems = [];
                return false;
            }

            restoredItems.Add(restoredInstruction);
        }

        return instructionsByIndex.Values.All(q => q.Count == 0);
    }

    private void ResetInstructionIndex(string listKey)
    {
        if (string.Equals(listKey, _activeSchedule ?? string.Empty, StringComparison.Ordinal))
        {
            _instructionIndex = 0;
        }

        if (_listStates.TryGetValue(listKey, out ListState savedState))
        {
            _listStates[listKey] = savedState with { InstructionIndex = 0 };
        }
    }

    private static List<SequenceGroup> GetSequenceGroups(
        List<YamlPlayoutInstruction> instructions,
        bool shuffledOnly) =>
        instructions
            .Select((instruction, index) => new IndexedInstruction(instruction, index))
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.Instruction.SequenceKey) &&
                (!shuffledOnly || x.Instruction.SequenceShuffled))
            .GroupBy(x => x.Instruction.SequenceGuid)
            .OrderBy(g => g.Min(x => x.Index))
            .Select(g => new SequenceGroup(
                g.First().Instruction.SequenceKey,
                g.OrderBy(x => x.Index).ToList()))
            .ToList();

    // switch to the playout list for the given schedule (null => default playout)
    public void SwitchToSchedule(string scheduleName)
    {
        // snapshot the state (position + ambient modifiers) of the list we're leaving
        string currentKey = _activeSchedule ?? string.Empty;
        _listStates[currentKey] = CaptureState();

        _activeSchedule = scheduleName;

        if (scheduleName is null)
        {
            _currentInstructions = null;
        }
        else
        {
            _currentInstructions = Definition.Schedules
                .Filter(s => string.Equals(s.Name, scheduleName, StringComparison.Ordinal))
                .Map(s => s.Playout)
                .HeadOrNone()
                .IfNone(Definition.Playout);
        }

        string targetKey = scheduleName ?? string.Empty;
        if (_listStates.TryGetValue(targetKey, out ListState savedState))
        {
            // resume where this list left off, including its ambient modifiers
            RestoreState(savedState);
        }
        else
        {
            // first time entering this list; start fresh with no ambient modifiers
            _instructionIndex = 0;
            _visitedInstructions = [];
            _channelWatermarkIds.Clear();
            _graphicsElements.Clear();
            _fillerKind.Clear();
            _preRollSequence = Option<string>.None;
            _postRollSequence = Option<string>.None;
            _midRollSequence = Option<MidRollSequence>.None;
        }
    }

    private ListState CaptureState() =>
        new(
            _instructionIndex,
            [.. _visitedInstructions],
            [.. _channelWatermarkIds],
            new Dictionary<int, string>(_graphicsElements),
            [.. _fillerKind],
            _preRollSequence,
            _postRollSequence,
            _midRollSequence);

    private void RestoreState(ListState state)
    {
        _instructionIndex = state.InstructionIndex;

        _visitedInstructions = [.. state.VisitedInstructions];

        _channelWatermarkIds.Clear();
        foreach (int id in state.ChannelWatermarkIds)
        {
            _channelWatermarkIds.Add(id);
        }

        _graphicsElements.Clear();
        foreach ((int id, string variables) in state.GraphicsElements)
        {
            _graphicsElements[id] = variables;
        }

        _fillerKind.Clear();
        // stack was captured top-first; push in reverse to preserve order
        for (int i = state.FillerKind.Count - 1; i >= 0; i--)
        {
            _fillerKind.Push(state.FillerKind[i]);
        }

        _preRollSequence = state.PreRollSequence;
        _postRollSequence = state.PostRollSequence;
        _midRollSequence = state.MidRollSequence;
    }

    public int PeekNextGuideGroup()
    {
        if (_guideGroupLocked)
        {
            return _guideGroup;
        }

        int result = _guideGroup + 1;
        if (result > 1000)
        {
            result = 1;
        }

        return result;
    }

    public void AdvanceGuideGroup()
    {
        if (_guideGroupLocked)
        {
            return;
        }

        _guideGroup++;
        if (_guideGroup > 1000)
        {
            _guideGroup = 1;
        }
    }

    public void LockGuideGroup(bool advance = true)
    {
        if (advance)
        {
            AdvanceGuideGroup();
        }

        _guideGroupLocked = true;
    }

    public void UnlockGuideGroup() => _guideGroupLocked = false;

    public void SetChannelWatermarkId(int id) => _channelWatermarkIds.Add(id);
    public void RemoveChannelWatermarkId(int id) => _channelWatermarkIds.Remove(id);
    public void ClearChannelWatermarkIds() => _channelWatermarkIds.Clear();
    public List<int> GetChannelWatermarkIds() => _channelWatermarkIds.ToList();

    public void SetGraphicsElement(int id, string variablesJson) => _graphicsElements[id] = variablesJson;
    public void RemoveGraphicsElement(int id) => _graphicsElements.Remove(id);
    public void ClearGraphicsElements() => _graphicsElements.Clear();
    public IReadOnlyDictionary<int, string> GetGraphicsElements() => _graphicsElements;

    public void SetPreRollSequence(string sequence) => _preRollSequence = sequence;
    public void ClearPreRollSequence() => _preRollSequence = Option<string>.None;
    public Option<string> GetPreRollSequence() => _preRollSequence;

    public void SetPostRollSequence(string sequence) => _postRollSequence = sequence;
    public void ClearPostRollSequence() => _postRollSequence = Option<string>.None;
    public Option<string> GetPostRollSequence() => _postRollSequence;

    public void SetMidRollSequence(MidRollSequence sequence) => _midRollSequence = sequence;
    public void ClearMidRollSequence() => _midRollSequence = Option<MidRollSequence>.None;
    public Option<MidRollSequence> GetMidRollSequence() => _midRollSequence;

    public void PushFillerKind(FillerKind fillerKind) => _fillerKind.Push(fillerKind);
    public void PopFillerKind() => _fillerKind.Pop();

    public Option<FillerKind> GetFillerKind() =>
        _fillerKind.TryPeek(out FillerKind fillerKind) ? fillerKind : Option<FillerKind>.None;

    public string Serialize()
    {
        string preRollSequence = null;
        foreach (string sequence in _preRollSequence)
        {
            preRollSequence = sequence;
        }

        // capture the current active list index alongside the other saved list indices
        var scheduleIndices = _listStates.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.InstructionIndex);
        scheduleIndices[_activeSchedule ?? string.Empty] = _instructionIndex;

        var state = new State(
            _instructionIndex,
            _guideGroup,
            _guideGroupLocked,
            _channelWatermarkIds.ToList(),
            preRollSequence,
            _activeSchedule,
            scheduleIndices,
            CaptureSequenceOrders(),
            _listFingerprints.Count > 0 ? new Dictionary<string, string>(_listFingerprints) : null);

        return JsonConvert.SerializeObject(state, Formatting.None, JsonSettings);
    }

    public void Reset(PlayoutAnchor anchor, DateTimeOffset start)
    {
        CurrentTime = new DateTimeOffset(anchor.NextStart, TimeSpan.Zero).ToLocalTime();

        if (string.IsNullOrWhiteSpace(anchor.Context))
        {
            return;
        }

        State state = JsonConvert.DeserializeObject<State>(anchor.Context);
        if (state.ChannelWatermarkIds is null)
        {
            state = state with { ChannelWatermarkIds = [] };
        }

        foreach (int instructionIndex in Optional(state.InstructionIndex))
        {
            _instructionIndex = instructionIndex;
        }

        foreach (int guideGroup in Optional(state.GuideGroup))
        {
            _guideGroup = guideGroup;
        }

        foreach (bool guideGroupLocked in Optional(state.GuideGroupLocked))
        {
            _guideGroupLocked = guideGroupLocked;
        }

        foreach (int channelWatermarkId in state.ChannelWatermarkIds)
        {
            _channelWatermarkIds.Add(channelWatermarkId);
        }

        foreach (string preRollSequence in Optional(state.PreRollSequence))
        {
            _preRollSequence = preRollSequence;
        }

        _listFingerprintsToRestore = state.ListFingerprints;
        _sequenceOrdersToRestore = state.SequenceOrders;

        // restore saved instruction indices for each playout list
        if (state.ScheduleIndices is not null)
        {
            foreach ((string key, int index) in state.ScheduleIndices)
            {
                _listStates[key] = new ListState(
                    index,
                    [],
                    [],
                    new Dictionary<int, string>(),
                    [],
                    Option<string>.None,
                    Option<string>.None,
                    Option<MidRollSequence>.None);
            }
        }

        // restore the active schedule and point the current instruction list at it
        _activeSchedule = state.ActiveSchedule;
        if (_activeSchedule is not null)
        {
            _currentInstructions = Definition.Schedules
                .Filter(s => string.Equals(s.Name, _activeSchedule, StringComparison.Ordinal))
                .Map(s => s.Playout)
                .HeadOrNone()
                .IfNone(Definition.Playout);
        }
    }

    private Dictionary<string, List<SequenceOrder>> CaptureSequenceOrders()
    {
        var result = new Dictionary<string, List<SequenceOrder>>();
        foreach ((string listKey, List<YamlPlayoutInstruction> instructions) in GetInstructionLists())
        {
            CaptureSequenceOrders(result, listKey, instructions);
        }

        return result.Count > 0 ? result : null;
    }

    private static void CaptureSequenceOrders(
        Dictionary<string, List<SequenceOrder>> result,
        string listKey,
        List<YamlPlayoutInstruction> instructions)
    {
        List<SequenceOrder> sequenceOrders = GetSequenceGroups(instructions, true)
            .Select(g => new SequenceOrder(
                g.Sequence,
                g.Items.Select(x => x.Instruction.SequenceIndex).ToList()))
            .ToList();

        if (sequenceOrders.Count > 0)
        {
            result[listKey] = sequenceOrders;
        }
    }

    public record State(
        int? InstructionIndex,
        int? GuideGroup,
        bool? GuideGroupLocked,
        List<int> ChannelWatermarkIds,
        string PreRollSequence,
        string ActiveSchedule = null,
        Dictionary<string, int> ScheduleIndices = null,
        Dictionary<string, List<SequenceOrder>> SequenceOrders = null,
        Dictionary<string, string> ListFingerprints = null);

    public record SequenceOrder(string Sequence, List<int> Order);

    public record MidRollSequence(string Sequence, string Expression);

    private sealed record IndexedInstruction(YamlPlayoutInstruction Instruction, int Index);

    private sealed record SequenceGroup(string Sequence, List<IndexedInstruction> Items);

    // in-memory snapshot of a playout list's position and ambient modifier state,
    // used to resume each list cleanly when switching between schedules during a build
    private sealed record ListState(
        int InstructionIndex,
        System.Collections.Generic.HashSet<int> VisitedInstructions,
        System.Collections.Generic.HashSet<int> ChannelWatermarkIds,
        Dictionary<int, string> GraphicsElements,
        List<FillerKind> FillerKind,
        Option<string> PreRollSequence,
        Option<string> PostRollSequence,
        Option<MidRollSequence> MidRollSequence);
}

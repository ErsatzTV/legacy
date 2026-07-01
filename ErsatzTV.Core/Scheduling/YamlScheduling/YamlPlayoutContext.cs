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
    private readonly Dictionary<string, ListState> _listStates = [];

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
            [.._visitedInstructions],
            [.._channelWatermarkIds],
            new Dictionary<int, string>(_graphicsElements),
            [.._fillerKind],
            _preRollSequence,
            _postRollSequence,
            _midRollSequence);

    private void RestoreState(ListState state)
    {
        _instructionIndex = state.InstructionIndex;

        _visitedInstructions = [..state.VisitedInstructions];

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
            scheduleIndices);

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

    public record State(
        int? InstructionIndex,
        int? GuideGroup,
        bool? GuideGroupLocked,
        List<int> ChannelWatermarkIds,
        string PreRollSequence,
        string ActiveSchedule = null,
        Dictionary<string, int> ScheduleIndices = null);

    public record MidRollSequence(string Sequence, string Expression);

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
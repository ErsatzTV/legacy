using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Interfaces.Scripting;
using ErsatzTV.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.Scheduling;

public class MultiEpisodeShuffleCollectionEnumerator : IMediaCollectionEnumerator
{
    private readonly CancellationToken _cancellationToken;
    private readonly Lazy<Option<TimeSpan>> _lazyMinimumDuration;
    private readonly ILogger _logger;
    private readonly int _mediaItemCount;
    private readonly Dictionary<int, List<MediaItem>> _mediaItemGroups;
    private readonly List<MediaItem> _ungrouped;
    private CloneableRandom _random;
    private IList<MediaItem> _shuffled;

    public MultiEpisodeShuffleCollectionEnumerator(
        ICollection<MediaItem> mediaItems,
        CollectionEnumeratorState state,
        IScriptEngine scriptEngine,
        string scriptFile,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        CurrentIncludeInProgramGuide = Option<bool>.None;

        _logger = logger;
        _cancellationToken = cancellationToken;

        if (!string.IsNullOrWhiteSpace(scriptFile))
        {
            scriptEngine.Load(scriptFile);
        }

        var numParts = (int)(double)scriptEngine.GetValue("numParts");

        _mediaItemGroups = new Dictionary<int, List<MediaItem>>();
        for (var i = 1; i <= numParts; i++)
        {
            _mediaItemGroups.Add(i, new List<MediaItem>());
        }

        _ungrouped = new List<MediaItem>();
        _mediaItemCount = mediaItems.Count;

        IList<Episode> validEpisodes = mediaItems
            .OfType<Episode>()
            .Filter(e => e.Season is not null && e.EpisodeMetadata is not null && e.EpisodeMetadata.Count == 1)
            .ToList();
        foreach (Episode episode in validEpisodes)
        {
            // prep script params
            int seasonNumber = episode.Season.SeasonNumber;
            int episodeNumber = episode.EpisodeMetadata[0].EpisodeNumber;

            // call the script function, and if we get a part (group) number back, use it
            if (scriptEngine.Invoke("partNumberForEpisode", seasonNumber, episodeNumber) is double result)
            {
                _mediaItemGroups[(int)result].Add(episode);
            }
            else
            {
                _ungrouped.Add(episode);
            }
        }

        // add everything else
        _ungrouped.AddRange(mediaItems.Except(validEpisodes));

        if (state.Index >= _mediaItemCount)
        {
            state.Index = 0;
            state.Seed = new Random(state.Seed).Next();
        }

        _random = new CloneableRandom(state.Seed);
        _shuffled = Shuffle(_random);
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
            _random = new CloneableRandom(state.Seed);
            _shuffled = Shuffle(_random);
        }

        State.Index = state.Index;
        State.Started = state.Started;
    }

    public string SchedulingContextName => "Multi-Episode Shuffle";

    public CollectionEnumeratorState State { get; }

    public Option<MediaItem> Current => _shuffled.Any() ? _shuffled[State.Index % _mediaItemCount] : None;
    public Option<bool> CurrentIncludeInProgramGuide { get; }

    public void MoveNext(Option<DateTimeOffset> scheduledAt)
    {
        if ((State.Index + 1) % _mediaItemCount == 0)
        {
            Option<MediaItem> tail = Current;

            State.Index = 0;
            do
            {
                State.Seed = _random.Next();
                _random = new CloneableRandom(State.Seed);
                _shuffled = Shuffle(_random);
            } while (!_cancellationToken.IsCancellationRequested && _mediaItemCount > 1 &&
                     Current.Map(x => x.Id) == tail.Map(x => x.Id));
        }
        else
        {
            State.Index++;
        }

        State.Index %= _mediaItemCount;
    }

    public Option<TimeSpan> MinimumDuration => _lazyMinimumDuration.Value;

    public int Count => _shuffled.Count;

    private IList<MediaItem> Shuffle(CloneableRandom random)
    {
        int maxGroupNumber = _mediaItemGroups.Max(a => a.Key);
        var shuffledGroups = new List<IList<MediaItem>>();
        for (var i = 1; i <= maxGroupNumber; i++)
        {
            shuffledGroups.Add(Shuffle(_mediaItemGroups[i], random));
        }

        int minItems = shuffledGroups.Min(g => g.Count);
        if (shuffledGroups.Any(g => g.Count != minItems))
        {
            _logger.LogError("Multi Episode Groups are different sizes; shuffle will not perform correctly!");
        }

        // convert shuffled "groups" into groups that can be used for scheduling
        var copy = new GroupedMediaItem[minItems + _ungrouped.Count];
        for (var i = 0; i < minItems; i++)
        {
            var group = new GroupedMediaItem(shuffledGroups[0][i], null);
            for (var j = 1; j < shuffledGroups.Count; j++)
            {
                group.Additional.Add(shuffledGroups[j][i]);
            }

            copy[i] = group;
        }

        // convert all ungrouped into groups that can be used for scheduling
        for (var i = 0; i < _ungrouped.Count; i++)
        {
            MediaItem ungrouped = _ungrouped[i];
            copy[minItems + i] = new GroupedMediaItem(ungrouped, null);
        }

        // perform shuffle
        int n = copy.Length;
        while (n > 1)
        {
            n--;
            int k = random.Next(n + 1);
            (copy[k], copy[n]) = (copy[n], copy[k]);
        }

        // flatten
        return GroupedMediaItem.FlattenGroups(copy, _mediaItemCount);
    }

    private static MediaItem[] Shuffle(IEnumerable<MediaItem> mediaItems, CloneableRandom random)
    {
        MediaItem[] copy = mediaItems.ToArray();

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

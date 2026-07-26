using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.Engine;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

// marathon content builds a PlaylistEnumerator over one playlist item per group, so it inherits that
// enumerator's save/restore behaviour; these cover the state handling, not the grouping
[TestFixture]
public class MarathonEnumeratorTests
{
    private const int Seed = 987654321;

    [SetUp]
    public void SetUp() => _cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private CancellationToken _cancellationToken;

    // a cycle completes when every media item has been played, which resets the index to 0
    [Test]
    public async Task Marathon_Should_Complete_A_Cycle()
    {
        List<MediaItem> mediaItems =
        [
            .. Show(showId: 1, seasonNumber: 1, episodes: 3, firstId: 10),
            .. Show(showId: 2, seasonNumber: 1, episodes: 3, firstId: 20)
        ];

        PlaylistEnumerator enumerator = await CreateMarathon(mediaItems);

        List<int> indexes = Advance(enumerator, 12);

        indexes.ShouldContain(0, "the index never returned to 0, so the cycle never completed");
    }

    // season 0 is filtered out by the season/episode enumerator but still counted as an item the cycle
    // is waiting on, so the cycle can never complete
    [Test]
    public async Task Marathon_Should_Complete_A_Cycle_With_Specials()
    {
        List<MediaItem> mediaItems =
        [
            .. Show(showId: 1, seasonNumber: 1, episodes: 3, firstId: 10),
            .. Show(showId: 1, seasonNumber: 0, episodes: 1, firstId: 15),
            .. Show(showId: 2, seasonNumber: 1, episodes: 3, firstId: 20)
        ];

        PlaylistEnumerator enumerator = await CreateMarathon(mediaItems);

        List<int> indexes = Advance(enumerator, 60);

        indexes.ShouldContain(0, "the index never returned to 0, so the cycle never completed");
    }

    // the flood and duration schedulers hand back a snapshot when an item doesn't fit, so ResetState
    // has to actually rewind
    [Test]
    public async Task Marathon_Should_Rewind_On_ResetState()
    {
        List<MediaItem> mediaItems =
        [
            .. Show(showId: 1, seasonNumber: 1, episodes: 3, firstId: 10),
            .. Show(showId: 2, seasonNumber: 1, episodes: 3, firstId: 20)
        ];

        PlaylistEnumerator enumerator = await CreateMarathon(mediaItems);


        enumerator.MoveNext(Option<DateTimeOffset>.None);

        CollectionEnumeratorState snapshot = enumerator.State.Clone();
        int expected = enumerator.Current.Map(mi => mi.Id).IfNone(-1);

        enumerator.MoveNext(Option<DateTimeOffset>.None);
        enumerator.MoveNext(Option<DateTimeOffset>.None);

        enumerator.ResetState(snapshot);

        enumerator.Current.Map(mi => mi.Id).IfNone(-1).ShouldBe(expected);
    }

    [Test]
    public async Task Marathon_Should_Resume_Where_It_Left_Off()
    {
        List<MediaItem> mediaItems =
        [
            .. Show(showId: 1, seasonNumber: 1, episodes: 3, firstId: 10),
            .. Show(showId: 2, seasonNumber: 1, episodes: 3, firstId: 20)
        ];

        PlaylistEnumerator reference = await CreateMarathon(mediaItems);

        var sequence = new List<int>();
        var states = new List<CollectionEnumeratorState>();
        for (var i = 0; i < 12; i++)
        {
            states.Add(reference.State.Clone());
            sequence.Add(reference.Current.Map(mi => mi.Id).IfNone(-1));
            reference.MoveNext(Option<DateTimeOffset>.None);
        }

        var mismatches = new List<string>();
        for (var i = 0; i < states.Count; i++)
        {
            PlaylistEnumerator resumed = await CreateMarathon(mediaItems, states[i]);
            int actual = resumed.Current.Map(mi => mi.Id).IfNone(-1);
            if (actual != sequence[i])
            {
                mismatches.Add($"position {i} (index {states[i].Index}): expected {sequence[i]}, got {actual}");
            }
        }

        mismatches.ShouldBeEmpty(string.Join("; ", mismatches));
    }

    private async Task<PlaylistEnumerator> CreateMarathon(
        List<MediaItem> mediaItems,
        CollectionEnumeratorState state = null)
    {
        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();
        var helper = new MarathonHelper(repo);

        Option<PlaylistEnumerator> maybeEnumerator = await helper.GetEnumerator(
            mediaItems,
            MarathonGroupBy.Show,
            marathonShuffleGroups: false,
            marathonShuffleItems: false,
            marathonBatchSize: Option<int>.None,
            state ?? new CollectionEnumeratorState { Seed = Seed, Index = 0 },
            _cancellationToken);

        return maybeEnumerator.IfNone(() => throw new InvalidOperationException("no marathon enumerator"));
    }

    private static List<int> Advance(PlaylistEnumerator enumerator, int count)
    {
        var indexes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            enumerator.MoveNext(Option<DateTimeOffset>.None);
            indexes.Add(enumerator.State.Index);
        }

        return indexes;
    }

    private static List<MediaItem> Show(int showId, int seasonNumber, int episodes, int firstId)
    {
        var seasonId = showId * 100 + seasonNumber;
        var season = new Season { Id = seasonId, ShowId = showId, SeasonNumber = seasonNumber };

        return Enumerable.Range(0, episodes)
            .Map(i => (MediaItem)new Episode
            {
                Id = firstId + i,
                Season = season,
                SeasonId = seasonId,
                EpisodeMetadata = [new EpisodeMetadata { EpisodeNumber = i + 1 }],
                MediaVersions =
                [
                    new MediaVersion
                    {
                        Duration = TimeSpan.FromMinutes(30),
                        MediaFiles = [new MediaFile { Path = $"/fake/path/{firstId + i}" }],
                        Chapters = []
                    }
                ]
            })
            .ToList();
    }
}

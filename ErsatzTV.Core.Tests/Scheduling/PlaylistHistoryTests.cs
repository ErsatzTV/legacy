using System.Collections.Immutable;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.BlockScheduling;
using ErsatzTV.Core.Scheduling.Engine;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

// the scripted, block and yaml schedulers each restore a playlist from history with the same 3 lines.
// a playlist position is the index of the primary history row; a child index counts the items of one
// collection, and PlaylistEnumerator.ResetState with a child index puts every child back to the cycle
// start
[TestFixture]
public class PlaylistHistoryTests
{
    private const int PlayoutSeed = 12345;

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ItemDuration = TimeSpan.FromMinutes(30);

    [SetUp]
    public void SetUp() => _cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private CancellationToken _cancellationToken;

    // a user reported this case: a sequential schedule with marathon pools repeated some episodes and
    // skipped others after a rebuild
    [Test]
    public async Task Marathon_Should_Resume_Where_The_Previous_Build_Stopped()
    {
        const int BUILD_ONE_COUNT = 20;
        const int COMPARE_COUNT = 12;

        IMediaCollectionRepository repo = FakeShowRepository(shows: 6, episodesPerShow: 8);
        YamlPlayoutContentMarathonItem marathon = MarathonContent(shows: 6);
        var definition = new YamlPlayoutDefinition { Content = [marathon] };
        var playout = new Playout { Id = 1, Seed = PlayoutSeed, PlayoutHistory = [] };

        var buildOneContext = new YamlPlayoutContext(playout, definition, 1) { CurrentTime = Start };
        var buildOneCache = new EnumeratorCache(repo, NullLogger.Instance);
        PlaylistEnumerator buildOne = await GetPlaylistEnumerator(buildOneCache, buildOneContext, marathon.Key);

        var playedInBuildOne = new List<int>();
        var history = new List<PlayoutHistory>();
        DateTimeOffset currentTime = Start;
        for (var i = 0; i < BUILD_ONE_COUNT; i++)
        {
            playedInBuildOne.Add(CurrentId(buildOne));
            history.AddRange(RecordHistory(buildOneContext, marathon.Key, buildOne, currentTime));
            buildOne.MoveNext(currentTime);
            currentTime += ItemDuration;
        }

        // the live enumerator never lost its position, so what it plays next is the correct order
        List<int> expected = Take(buildOne, COMPARE_COUNT);

        // build 2 starts with a new enumerator that has only the saved history
        var buildTwoContext = new YamlPlayoutContext(playout, definition, 1) { CurrentTime = currentTime };
        var buildTwoCache = new EnumeratorCache(repo, NullLogger.Instance);
        var applyHistory = new YamlPlayoutApplyHistoryHandler(buildTwoCache);

        bool applied = await applyHistory.Handle(
            history,
            buildTwoContext,
            marathon,
            NullLogger<SequentialPlayoutBuilder>.Instance,
            _cancellationToken);

        applied.ShouldBeTrue();

        PlaylistEnumerator buildTwo = await GetPlaylistEnumerator(buildTwoCache, buildTwoContext, marathon.Key);
        List<int> actual = Take(buildTwo, COMPARE_COUNT);

        actual.ShouldBe(
            expected,
            $"build 1 played [{string.Join(", ", playedInBuildOne)}]");
    }

    // a shuffled playlist gets a new seed at the end of each cycle.
    // 6 shows with 2 episodes give a cycle of 12 items, and build 1 stops in cycle 2.
    // the test needs 4 groups or more, because ShufflePlaylistItems refuses an order that starts with
    // the last group of the previous order.
    [Test]
    public async Task Marathon_Should_Resume_Inside_A_Later_Cycle()
    {
        const int BUILD_ONE_COUNT = 14;
        const int COMPARE_COUNT = 12;

        IMediaCollectionRepository repo = FakeShowRepository(shows: 6, episodesPerShow: 2);
        YamlPlayoutContentMarathonItem marathon = MarathonContent(shows: 6);
        var definition = new YamlPlayoutDefinition { Content = [marathon] };
        var playout = new Playout { Id = 1, Seed = PlayoutSeed, PlayoutHistory = [] };

        var buildOneContext = new YamlPlayoutContext(playout, definition, 1) { CurrentTime = Start };
        var buildOneCache = new EnumeratorCache(repo, NullLogger.Instance);
        PlaylistEnumerator buildOne = await GetPlaylistEnumerator(buildOneCache, buildOneContext, marathon.Key);

        int seedAtStart = buildOne.State.Seed;

        var playedInBuildOne = new List<int>();
        var history = new List<PlayoutHistory>();
        DateTimeOffset currentTime = Start;
        for (var i = 0; i < BUILD_ONE_COUNT; i++)
        {
            playedInBuildOne.Add(CurrentId(buildOne));
            history.AddRange(RecordHistory(buildOneContext, marathon.Key, buildOne, currentTime));
            buildOne.MoveNext(currentTime);
            currentTime += ItemDuration;
        }

        // the build must cross a cycle end, or the seed does not change and the test shows nothing
        buildOne.State.Seed.ShouldNotBe(seedAtStart);

        List<int> expected = Take(buildOne, COMPARE_COUNT);

        var buildTwoContext = new YamlPlayoutContext(playout, definition, 1) { CurrentTime = currentTime };
        var buildTwoCache = new EnumeratorCache(repo, NullLogger.Instance);
        var applyHistory = new YamlPlayoutApplyHistoryHandler(buildTwoCache);

        bool applied = await applyHistory.Handle(
            history,
            buildTwoContext,
            marathon,
            NullLogger<SequentialPlayoutBuilder>.Instance,
            _cancellationToken);

        applied.ShouldBeTrue();

        PlaylistEnumerator buildTwo = await GetPlaylistEnumerator(buildTwoCache, buildTwoContext, marathon.Key);
        List<int> actual = Take(buildTwo, COMPARE_COUNT);

        actual.ShouldBe(
            expected,
            $"build 1 played [{string.Join(", ", playedInBuildOne)}]");
    }

    // the block scheduler restores playlist filler with its own copy of the same 3 lines
    [Test]
    public async Task Block_Playlist_Filler_Should_Resume_Where_The_Previous_Build_Stopped()
    {
        const int PLAYLIST_ID = 7;
        const int BUILD_ONE_COUNT = 11;
        const int COMPARE_COUNT = 8;
        const string HISTORY_KEY = "block-playlist-filler";

        IMediaCollectionRepository repo = FakePlaylistRepository(PLAYLIST_ID, collections: 3, itemsPerCollection: 4);

        PlaylistEnumerator buildOne = (PlaylistEnumerator)await BlockPlayoutEnumerator.PlaylistForFiller(
            repo,
            PLAYLIST_ID,
            Start,
            PlayoutSeed,
            [],
            seedOffset: 0,
            HISTORY_KEY,
            _cancellationToken);

        var history = new List<PlayoutHistory>();
        DateTimeOffset currentTime = Start;
        for (var i = 0; i < BUILD_ONE_COUNT; i++)
        {
            history.Add(BlockFillerHistoryFor(buildOne, HISTORY_KEY, currentTime));
            buildOne.MoveNext(currentTime);
            currentTime += ItemDuration;
        }

        List<int> expected = Take(buildOne, COMPARE_COUNT);

        var buildTwo = (PlaylistEnumerator)await BlockPlayoutEnumerator.PlaylistForFiller(
            repo,
            PLAYLIST_ID,
            currentTime,
            PlayoutSeed,
            history,
            seedOffset: 0,
            HISTORY_KEY,
            _cancellationToken);

        Take(buildTwo, COMPARE_COUNT).ShouldBe(expected);
    }

    // one ResetState call for each child would put every child back to the cycle start, so the second
    // child would discard the position of the first
    [Test]
    public async Task Applying_History_Should_Restore_Every_Childs_Position()
    {
        const int BUILD_ONE_COUNT = 11;
        const string HISTORY_KEY = "playlist";

        IMediaCollectionRepository repo = FakePlaylistRepository(playlistId: 1, collections: 3, itemsPerCollection: 4);
        Dictionary<PlaylistItem, List<MediaItem>> itemMap = await repo.GetPlaylistItemMap(1, _cancellationToken);

        SchedulingEngine engine = CreateEngine(repo);
        PlaylistEnumerator buildOne = await CreatePlaylistEnumerator(repo, itemMap, _cancellationToken);

        DateTimeOffset currentTime = Start;
        var history = new List<PlayoutHistory>();
        for (var i = 0; i < BUILD_ONE_COUNT; i++)
        {
            history.AddRange(ScriptedHistoryFor(engine, buildOne, HISTORY_KEY, currentTime));
            buildOne.MoveNext(currentTime);
            currentTime += ItemDuration;
        }

        List<int> expected = ChildItemIds(buildOne);

        // the children need different positions, or a rewind to the cycle start would not show
        expected.Distinct().Count().ShouldBeGreaterThan(1);

        PlaylistEnumerator buildTwo = await CreatePlaylistEnumerator(repo, itemMap, _cancellationToken);
        ApplyScriptedHistory(engine, history, currentTime, HISTORY_KEY, itemMap, buildTwo);

        ChildItemIds(buildTwo).ShouldBe(expected, "at least one child was rewound to the cycle start");
    }

    // the index of the primary history row is the cycle position of the playlist, not the slot number
    // of the current child
    [Test]
    public async Task Playlist_Should_Restore_Its_Cycle_Position_From_History()
    {
        const int BUILD_ONE_COUNT = 7;

        IMediaCollectionRepository repo = FakePlaylistRepository(playlistId: 1, collections: 3, itemsPerCollection: 4);
        Dictionary<PlaylistItem, List<MediaItem>> itemMap = await repo.GetPlaylistItemMap(1, _cancellationToken);

        PlaylistEnumerator buildOne = await CreatePlaylistEnumerator(repo, itemMap, _cancellationToken);

        DateTimeOffset currentTime = Start;
        var history = new List<PlayoutHistory>();
        for (var i = 0; i < BUILD_ONE_COUNT; i++)
        {
            history.Add(BlockFillerHistoryFor(buildOne, "playlist", currentTime));
            buildOne.MoveNext(currentTime);
            currentTime += ItemDuration;
        }

        int expectedPlaylistIndex = buildOne.State.Index;

        var buildTwo = (PlaylistEnumerator)await BlockPlayoutEnumerator.PlaylistForFiller(
            repo,
            1,
            currentTime,
            PlayoutSeed,
            history,
            seedOffset: 0,
            "playlist",
            _cancellationToken);

        buildTwo.State.Index.ShouldBe(expectedPlaylistIndex);
    }

    // the scripted scheduler reaches the same 3 lines through SchedulingEngine.ApplyPlaylistHistory
    [Test]
    public async Task Scripted_Playlist_Should_Resume_Where_The_Previous_Build_Stopped()
    {
        const int BUILD_ONE_COUNT = 11;
        const int COMPARE_COUNT = 8;
        const string HISTORY_KEY = "scripted-playlist";

        IMediaCollectionRepository repo = FakePlaylistRepository(playlistId: 1, collections: 3, itemsPerCollection: 4);
        Dictionary<PlaylistItem, List<MediaItem>> itemMap = await repo.GetPlaylistItemMap(1, _cancellationToken);

        SchedulingEngine engine = CreateEngine(repo);
        PlaylistEnumerator buildOne = await CreatePlaylistEnumerator(repo, itemMap, _cancellationToken);

        DateTimeOffset currentTime = Start;
        var history = new List<PlayoutHistory>();
        for (var i = 0; i < BUILD_ONE_COUNT; i++)
        {
            history.AddRange(ScriptedHistoryFor(engine, buildOne, HISTORY_KEY, currentTime));
            buildOne.MoveNext(currentTime);
            currentTime += ItemDuration;
        }

        List<int> expected = Take(buildOne, COMPARE_COUNT);

        PlaylistEnumerator buildTwo = await CreatePlaylistEnumerator(repo, itemMap, _cancellationToken);
        ApplyScriptedHistory(engine, history, currentTime, HISTORY_KEY, itemMap, buildTwo);

        Take(buildTwo, COMPARE_COUNT).ShouldBe(expected);
    }

    // with uneven collection sizes, a cycle can end while a child is part way through its own list.
    // a rewind to the cycle start does not make that position again.
    // item_order is shuffle by default for a marathon.
    // a shuffled child gets a new seed each time it wraps, so the replay cannot make that order again.
    [Test]
    [TestCase(PlaybackOrder.Chronological)]
    [TestCase(PlaybackOrder.Shuffle)]
    public async Task Shuffled_Playlist_Of_Uneven_Collections_Should_Resume_Inside_A_Later_Cycle(
        PlaybackOrder itemPlaybackOrder)
    {
        const string HISTORY_KEY = "uneven-playlist";

        IMediaCollectionRepository repo = FakeUnevenPlaylistRepository(itemPlaybackOrder);
        Dictionary<PlaylistItem, List<MediaItem>> itemMap = await repo.GetPlaylistItemMap(1, _cancellationToken);

        SchedulingEngine engine = CreateEngine(repo);
        PlaylistEnumerator buildOne = await CreateShuffledPlaylistEnumerator(repo, itemMap, _cancellationToken);

        int seedAtStart = buildOne.State.Seed;

        DateTimeOffset currentTime = Start;
        var history = new List<PlayoutHistory>();
        var cycles = 0;
        var played = 0;

        // stop part way into the third cycle
        while (cycles < 2 || played < 5)
        {
            history.Clear();
            history.AddRange(ScriptedHistoryFor(engine, buildOne, HISTORY_KEY, currentTime));
            buildOne.MoveNext(currentTime);
            currentTime += ItemDuration;

            if (cycles >= 2)
            {
                played++;
            }
            else if (buildOne.State.Index == 0)
            {
                cycles++;
            }
        }

        buildOne.State.Seed.ShouldNotBe(seedAtStart, "the playlist should have reshuffled");

        List<int> expected = Take(buildOne, 10);

        PlaylistEnumerator buildTwo = await CreateShuffledPlaylistEnumerator(repo, itemMap, _cancellationToken);
        ApplyScriptedHistory(engine, history, currentTime, HISTORY_KEY, itemMap, buildTwo);

        Take(buildTwo, 10).ShouldBe(expected);
    }

    private static async Task<PlaylistEnumerator> CreateShuffledPlaylistEnumerator(
        IMediaCollectionRepository repo,
        Dictionary<PlaylistItem, List<MediaItem>> itemMap,
        CancellationToken cancellationToken) =>
        await PlaylistEnumerator.Create(
            repo,
            itemMap,
            new CollectionEnumeratorState { Seed = PlayoutSeed, Index = 0 },
            shufflePlaylistItems: true,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            cancellationToken);

    private static IMediaCollectionRepository FakeUnevenPlaylistRepository(PlaybackOrder itemPlaybackOrder)
    {
        int[] sizes = [2, 3, 2, 4];

        Dictionary<PlaylistItem, List<MediaItem>> itemMap = Enumerable.Range(1, sizes.Length)
            .ToDictionary(
                collectionId => new PlaylistItem
                {
                    Id = collectionId,
                    Index = collectionId - 1,
                    PlaybackOrder = itemPlaybackOrder,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = collectionId,
                    IncludeInProgramGuide = true
                },
                collectionId => Enumerable.Range(0, sizes[collectionId - 1])
                    .Map(i => (MediaItem)FakeMovie(collectionId * 100 + i))
                    .ToList());

        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();
        repo.GetPlaylistItemMap(1, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(itemMap));

        return repo;
    }

    private static async Task<PlaylistEnumerator> CreatePlaylistEnumerator(
        IMediaCollectionRepository repo,
        Dictionary<PlaylistItem, List<MediaItem>> itemMap,
        CancellationToken cancellationToken) =>
        await PlaylistEnumerator.Create(
            repo,
            itemMap,
            new CollectionEnumeratorState { Seed = PlayoutSeed, Index = 0 },
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            cancellationToken);

    private static async Task<PlaylistEnumerator> GetPlaylistEnumerator(
        EnumeratorCache cache,
        YamlPlayoutContext context,
        string contentKey)
    {
        Option<IMediaCollectionEnumerator> maybeEnumerator =
            await cache.GetCachedEnumeratorForContent(context, contentKey, CancellationToken.None);

        return maybeEnumerator
            .Map(e => e as PlaylistEnumerator)
            .IfNone(() => throw new InvalidOperationException("no playlist enumerator"));
    }

    private static List<PlayoutHistory> RecordHistory(
        YamlPlayoutContext context,
        string contentKey,
        PlaylistEnumerator enumerator,
        DateTimeOffset startTime)
    {
        var playoutItem = new PlayoutItem
        {
            Start = startTime.UtcDateTime,
            Finish = (startTime + ItemDuration).UtcDateTime
        };

        MediaItem mediaItem = enumerator.Current.IfNone(() => throw new InvalidOperationException("no current item"));

        return HistoryRecorder.Record(
            context,
            contentKey,
            enumerator,
            playoutItem,
            mediaItem,
            NullLogger<SequentialPlayoutBuilder>.Instance);
    }

    private static SchedulingEngine CreateEngine(IMediaCollectionRepository repo) =>
        new(
            repo,
            Substitute.For<IGraphicsElementRepository>(),
            Substitute.For<IChannelRepository>(),
            NullLogger<SchedulingEngine>.Instance);

    private static List<PlayoutHistory> ScriptedHistoryFor(
        SchedulingEngine engine,
        PlaylistEnumerator enumerator,
        string historyKey,
        DateTimeOffset startTime)
    {
        var playoutItem = new PlayoutItem
        {
            Start = startTime.UtcDateTime,
            Finish = (startTime + ItemDuration).UtcDateTime
        };

        MediaItem mediaItem = enumerator.Current.IfNone(() => throw new InvalidOperationException("no current item"));

        return engine.GetHistoryForItem(
            new EnumeratorDetails(enumerator, historyKey, PlaybackOrder.None),
            playoutItem,
            mediaItem);
    }

    private static void ApplyScriptedHistory(
        SchedulingEngine engine,
        List<PlayoutHistory> history,
        DateTimeOffset currentTime,
        string historyKey,
        Dictionary<PlaylistItem, List<MediaItem>> itemMap,
        PlaylistEnumerator enumerator)
    {
        // ApplyPlaylistHistory reads the history and the current time from engine state
        engine.WithReferenceData(
            new PlayoutReferenceData(
                new Channel(Guid.NewGuid()) { Id = 1, Number = "1", Name = "Playlist history test" },
                Option<Deco>.None,
                [],
                [],
                null,
                [],
                history,
                TimeSpan.Zero));
        engine.BuildBetween(currentTime, currentTime.AddDays(1));

        engine.ApplyPlaylistHistory(
            historyKey,
            itemMap.ToImmutableDictionary(x => CollectionKey.ForPlaylistItem(x.Key), x => x.Value),
            enumerator);
    }

    // BlockPlayoutFillerBuilder writes one row for each item and no child rows, so the index of the
    // playlist is the only position it saves
    private static PlayoutHistory BlockFillerHistoryFor(
        PlaylistEnumerator enumerator,
        string historyKey,
        DateTimeOffset startTime) =>
        new()
        {
            PlaybackOrder = PlaybackOrder.Shuffle,
            Index = enumerator.State.Index,
            When = startTime.UtcDateTime,
            Finish = (startTime + ItemDuration).UtcDateTime,
            Key = historyKey,
            Details = HistoryDetails.ForMediaItem(
                enumerator.Current.IfNone(() => throw new InvalidOperationException("no current item")))
        };

    private static List<int> Take(PlaylistEnumerator enumerator, int count)
    {
        var result = new List<int>();
        for (var i = 0; i < count; i++)
        {
            result.Add(CurrentId(enumerator));
            enumerator.MoveNext(Option<DateTimeOffset>.None);
        }

        return result;
    }

    private static int CurrentId(PlaylistEnumerator enumerator) => enumerator.Current.Map(mi => mi.Id).IfNone(-1);

    private static List<int> ChildItemIds(PlaylistEnumerator enumerator) => enumerator.ChildEnumerators
        .Map(c => c.Enumerator.Current.Map(mi => mi.Id).IfNone(-1))
        .ToList();

    private static YamlPlayoutContentMarathonItem MarathonContent(int shows) =>
        new()
        {
            Key = "marathon",
            Marathon = "marathon",
            Guids = Enumerable.Range(1, shows)
                .Map(showId => new YamlPlayoutContentGuid { Source = "imdb", Value = $"show{showId}" })
                .ToList(),
            GroupBy = "show",
            ShuffleGroups = true,
            ItemOrder = "chronological",
            PlayAllItems = false
        };

    private static IMediaCollectionRepository FakeShowRepository(int shows, int episodesPerShow)
    {
        Dictionary<string, List<MediaItem>> byGuid = Enumerable.Range(1, shows)
            .ToDictionary(
                showId => $"imdb://show{showId}",
                showId => Episodes(showId, episodesPerShow));

        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();
        repo.GetShowItemsByShowGuids(Arg.Any<List<string>>())
            .Returns(call => Task.FromResult(((List<string>)call[0]).SelectMany(g => byGuid[g]).ToList()));

        return repo;
    }

    private static IMediaCollectionRepository FakePlaylistRepository(
        int playlistId,
        int collections,
        int itemsPerCollection)
    {
        Dictionary<PlaylistItem, List<MediaItem>> itemMap = Enumerable.Range(1, collections)
            .ToDictionary(
                collectionId => new PlaylistItem
                {
                    Id = collectionId,
                    Index = collectionId - 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = collectionId,
                    IncludeInProgramGuide = true
                },
                collectionId => Enumerable.Range(0, itemsPerCollection)
                    .Map(i => (MediaItem)FakeMovie(collectionId * 100 + i))
                    .ToList());

        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();
        repo.GetPlaylistItemMap(playlistId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(itemMap));

        return repo;
    }

    private static List<MediaItem> Episodes(int showId, int episodes)
    {
        int seasonId = showId * 100 + 1;
        var season = new Season { Id = seasonId, ShowId = showId, SeasonNumber = 1 };

        return Enumerable.Range(0, episodes)
            .Map(i => (MediaItem)new Episode
            {
                Id = showId * 100 + i,
                Season = season,
                SeasonId = seasonId,
                EpisodeMetadata =
                [
                    new EpisodeMetadata
                    {
                        EpisodeNumber = i + 1,
                        ReleaseDate = new DateTime(2020, 1, 1).AddDays(i)
                    }
                ],
                MediaVersions =
                [
                    new MediaVersion
                    {
                        Duration = ItemDuration,
                        MediaFiles = [new MediaFile { Path = $"/fake/path/{showId}-{i}" }],
                        Chapters = []
                    }
                ]
            })
            .ToList();
    }

    private static Movie FakeMovie(int id) => new()
    {
        Id = id,
        MediaVersions = [new MediaVersion { Duration = ItemDuration, MediaFiles = [], Chapters = [] }],
        MovieMetadata =
        [
            new MovieMetadata
            {
                Title = $"Movie {id}",
                ReleaseDate = new DateTime(2020, 1, 1).AddDays(id)
            }
        ]
    };

    // GetHistoryForItem is protected on the real content handler, so this subclass gives the test the
    // production writer instead of a copy
    private sealed class HistoryRecorder(EnumeratorCache enumeratorCache) : YamlPlayoutContentHandler(enumeratorCache)
    {
        public static List<PlayoutHistory> Record(
            YamlPlayoutContext context,
            string contentKey,
            IMediaCollectionEnumerator enumerator,
            PlayoutItem playoutItem,
            MediaItem mediaItem,
            ILogger<SequentialPlayoutBuilder> logger) =>
            GetHistoryForItem(context, contentKey, enumerator, playoutItem, mediaItem, logger);

        public override Task<bool> Handle(
            YamlPlayoutContext context,
            YamlPlayoutInstruction instruction,
            PlayoutBuildMode mode,
            Func<string, Task> executeSequence,
            ILogger<SequentialPlayoutBuilder> logger,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

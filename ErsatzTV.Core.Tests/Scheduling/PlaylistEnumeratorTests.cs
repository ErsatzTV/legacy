using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Scheduling;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class PlaylistEnumeratorTests
{
    [Test]
    public async Task Test_PlayAll_Before_Last_PlaylistItem()
    {
        // test a 1 item, b 2 items play all, c 2 items
        // a1, b1, b2, c1, a1, b1, b2, c2

        // this isn't needed for chronological, so no need to implement anything
        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();

        var playlistItemMap = new Dictionary<PlaylistItem, List<MediaItem>>
        {
            {
                new PlaylistItem
                {
                    Id = 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 1
                },
                [FakeMovie(10)]
            },
            {
                new PlaylistItem
                {
                    Id = 2,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = true,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 2
                },
                [FakeMovie(20), FakeMovie(21)]
            },
            {
                new PlaylistItem
                {
                    Id = 3,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 3
                },
                [FakeMovie(30), FakeMovie(31)]
            }
        };

        PlaylistEnumerator enumerator = await PlaylistEnumerator.Create(
            repo,
            playlistItemMap,
            new CollectionEnumeratorState(),
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            CancellationToken.None);

        var items = new List<int>();
        items.AddRange(enumerator.Current.Map(mi => mi.Id));

        enumerator.MoveNext(Option<DateTimeOffset>.None);
        while (enumerator.State.Index > 0)
        {
            items.AddRange(enumerator.Current.Map(mi => mi.Id));
            enumerator.MoveNext(Option<DateTimeOffset>.None);
        }

        items.Count.ShouldBe(8);
        items.ShouldBe([10, 20, 21, 30, 10, 20, 21, 31]);
    }

    [Test]
    public async Task Test_PlayAll_Last_PlaylistItem()
    {
        // test a 1 item, b 2 items, c 2 items play all
        // a1, b1, c1, c2, a1, b2, c1, c2

        // this isn't needed for chronological, so no need to implement anything
        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();

        var playlistItemMap = new Dictionary<PlaylistItem, List<MediaItem>>
        {
            {
                new PlaylistItem
                {
                    Id = 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 1
                },
                [FakeMovie(10)]
            },
            {
                new PlaylistItem
                {
                    Id = 2,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 2
                },
                [FakeMovie(20), FakeMovie(21)]
            },
            {
                new PlaylistItem
                {
                    Id = 3,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = true,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 3
                },
                [FakeMovie(30), FakeMovie(31)]
            }
        };

        PlaylistEnumerator enumerator = await PlaylistEnumerator.Create(
            repo,
            playlistItemMap,
            new CollectionEnumeratorState(),
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            CancellationToken.None);

        var items = new List<int>();
        items.AddRange(enumerator.Current.Map(mi => mi.Id));

        enumerator.MoveNext(Option<DateTimeOffset>.None);
        while (enumerator.State.Index > 0)
        {
            items.AddRange(enumerator.Current.Map(mi => mi.Id));
            enumerator.MoveNext(Option<DateTimeOffset>.None);
        }

        items.Count.ShouldBe(8);
        items.ShouldBe([10, 20, 30, 31, 10, 21, 30, 31]);
    }

    [Test]
    public async Task Shuffled_Playlist_Should_Honor_PlayAll()
    {
        // this isn't needed for chronological, so no need to implement anything
        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();

        var playlistItemMap = new Dictionary<PlaylistItem, List<MediaItem>>
        {
            {
                new PlaylistItem
                {
                    Id = 1,
                    Index = 0,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 1
                },
                [FakeMovie(10)]
            },
            {
                new PlaylistItem
                {
                    Id = 2,
                    Index = 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = true,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 2
                },
                [FakeMovie(20), FakeMovie(21)]
            },
            {
                new PlaylistItem
                {
                    Id = 3,
                    Index = 2,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 3
                },
                [FakeMovie(30)]
            }
        };

        var state = new CollectionEnumeratorState { Seed = 1 };

        PlaylistEnumerator enumerator = await PlaylistEnumerator.Create(
            repo,
            playlistItemMap,
            state,
            shufflePlaylistItems: true,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            CancellationToken.None);

        var items = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            items.AddRange(enumerator.Current.Map(mi => mi.Id));
            enumerator.MoveNext(Option<DateTimeOffset>.None);
        }

        // with seed 1, shuffle order of (1,2,3) is (2,3,1)
        // correct playout should be item 2 (all), item 3 (1), item 1 (1)
        // which is media items (20, 21), (30), (10)
        items.ShouldBe([20, 21, 30, 10]);
    }

    [Test]
    public async Task Shuffled_Playlist_Should_Honor_Custom_Count()
    {
        // this isn't needed for chronological, so no need to implement anything
        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();

        var playlistItemMap = new Dictionary<PlaylistItem, List<MediaItem>>
        {
            {
                new PlaylistItem
                {
                    Id = 1,
                    Index = 0,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    Count = 2,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 1
                },
                [FakeMovie(10), FakeMovie(11), FakeMovie(12)]
            },
            {
                new PlaylistItem
                {
                    Id = 2,
                    Index = 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 2
                },
                [FakeMovie(20)]
            },
            {
                new PlaylistItem
                {
                    Id = 3,
                    Index = 2,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 3
                },
                [FakeMovie(30)]
            }
        };

        var state = new CollectionEnumeratorState { Seed = 1 };

        PlaylistEnumerator enumerator = await PlaylistEnumerator.Create(
            repo,
            playlistItemMap,
            state,
            shufflePlaylistItems: true,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            CancellationToken.None);

        var items = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            items.AddRange(enumerator.Current.Map(mi => mi.Id));
            enumerator.MoveNext(Option<DateTimeOffset>.None);
        }

        // with seed 1, shuffle order of (1,2,3) is (2,3,1)
        // correct playout should be item 2 (1), item 3 (1), item 1 (2)
        // which is media items (20), (30), (10, 11)
        items.ShouldBe([20, 30, 10, 11]);
    }

    [Test]
    public async Task CountForFiller_Should_Honor_Custom_Count_And_PlayAll()
    {
        // this isn't needed for chronological, so no need to implement anything
        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();

        var playlistItemMap = new Dictionary<PlaylistItem, List<MediaItem>>
        {
            {
                new PlaylistItem
                {
                    Id = 1,
                    Index = 0,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    Count = 2,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 1
                },
                [FakeMovie(10), FakeMovie(11), FakeMovie(12)]
            },
            {
                new PlaylistItem
                {
                    Id = 2,
                    Index = 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 2
                },
                [FakeMovie(15)]
            },
            {
                new PlaylistItem
                {
                    Id = 3,
                    Index = 2,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 3
                },
                [FakeMovie(20)]
            },
            {
                new PlaylistItem
                {
                    Id = 4,
                    Index = 3,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = true,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 4
                },
                [FakeMovie(25), FakeMovie(26), FakeMovie(27)]
            }
        };

        var state = new CollectionEnumeratorState { Seed = 1 };

        PlaylistEnumerator enumerator = await PlaylistEnumerator.Create(
            repo,
            playlistItemMap,
            state,
            shufflePlaylistItems: true,
            batchSize: Option<int>.None,
            randomStartPoint: false,
            CancellationToken.None);

        enumerator.CountForFiller.ShouldBe(7);
    }

    [Test]
    public async Task RandomStartPoint_Should_Continue_After_Rebuild()
    {
        IMediaCollectionRepository repo = Substitute.For<IMediaCollectionRepository>();

        Dictionary<PlaylistItem, List<MediaItem>> BuildMap() => new()
        {
            {
                new PlaylistItem
                {
                    Id = 1,
                    Index = 0,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 1
                },
                [FakeMovie(10), FakeMovie(11), FakeMovie(12), FakeMovie(13), FakeMovie(14)]
            },
            {
                new PlaylistItem
                {
                    Id = 2,
                    Index = 1,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 2
                },
                [FakeMovie(15), FakeMovie(16), FakeMovie(17), FakeMovie(18), FakeMovie(19)]
            },
            {
                new PlaylistItem
                {
                    Id = 3,
                    Index = 2,
                    PlaybackOrder = PlaybackOrder.Chronological,
                    PlayAll = false,
                    CollectionType = CollectionType.Collection,
                    CollectionId = 3
                },
                [FakeMovie(20), FakeMovie(21), FakeMovie(22), FakeMovie(23), FakeMovie(24)]
            }
        };

        const int SEED = 987654321;

        // day 1: fresh build with a random start point
        PlaylistEnumerator day1 = await PlaylistEnumerator.Create(
            repo,
            BuildMap(),
            new CollectionEnumeratorState { Seed = SEED, Index = 0 },
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: true,
            CancellationToken.None);

        // capture a reference sequence and the state we would persist halfway through
        var reference = new List<int>();
        CollectionEnumeratorState persistedState = null;
        for (var i = 0; i < 12; i++)
        {
            reference.AddRange(day1.Current.Map(mi => mi.Id));
            day1.MoveNext(Option<DateTimeOffset>.None);
            if (i == 5)
            {
                persistedState = day1.State.Clone();
            }
        }

        // sanity: the random start point actually moved us off the natural start
        reference.First().ShouldNotBe(10);
        persistedState.Started.ShouldBeTrue();

        // day 2: rebuild from the persisted state; the offsets must be reproduced so we continue
        PlaylistEnumerator day2 = await PlaylistEnumerator.Create(
            repo,
            BuildMap(),
            persistedState,
            shufflePlaylistItems: false,
            batchSize: Option<int>.None,
            randomStartPoint: true,
            CancellationToken.None);

        var continued = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            continued.AddRange(day2.Current.Map(mi => mi.Id));
            day2.MoveNext(Option<DateTimeOffset>.None);
        }

        // day 2 must pick up exactly where day 1 left off, not reset toward the start
        continued.ShouldBe(reference.Skip(6).Take(6));
    }

    private static Movie FakeMovie(int id) => new()
    {
        Id = id,
        MediaVersions = [],
        MovieMetadata =
        [
            new MovieMetadata
            {
                ReleaseDate = new DateTime(2020, 1, id)
            }
        ]
    };
}

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

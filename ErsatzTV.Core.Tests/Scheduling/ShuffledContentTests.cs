using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class ShuffledContentTests
{
    [SetUp]
    public void SetUp() => _cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    // this seed will produce (shuffle) 1-10 in order
    private const int MagicSeed = 670596;

    private CancellationToken _cancellationToken;

    [Test]
    public void Episodes_Should_Not_Duplicate_When_Reshuffling()
    {
        List<MediaItem> contents = Episodes(10);

        // normally returns 10 5 7 4 3 6 2 8 9 1 1 (note duplicate 1 at end)
        var state = new CollectionEnumeratorState { Seed = 8 };

        var groupedMediaItems = contents.Map(mi => new GroupedMediaItem(mi, null)).ToList();
        var shuffledContent = new ShuffledMediaCollectionEnumerator(groupedMediaItems, state, _cancellationToken);

        var list = new List<int>();
        for (var i = 1; i <= 1000; i++)
        {
            shuffledContent.Current.IsSome.ShouldBeTrue();
            shuffledContent.Current.Do(x => list.Add(x.Id));
            shuffledContent.MoveNext(Option<DateTimeOffset>.None);
        }

        for (var i = 0; i < list.Count - 1; i++)
        {
            if (list[i] == list[i + 1])
            {
                Assert.Fail("List contains duplicate items");
            }
        }
    }

    [Test]
    public void Duplicate_Check_Should_Ignore_Single_Item()
    {
        List<MediaItem> contents = Episodes(1);

        var state = new CollectionEnumeratorState();

        var groupedMediaItems = contents.Map(mi => new GroupedMediaItem(mi, null)).ToList();
        var shuffledContent = new ShuffledMediaCollectionEnumerator(groupedMediaItems, state, _cancellationToken);

        var list = new List<int>();
        for (var i = 1; i <= 10; i++)
        {
            shuffledContent.Current.IsSome.ShouldBeTrue();
            shuffledContent.Current.Do(x => list.Add(x.Id));
            shuffledContent.MoveNext(Option<DateTimeOffset>.None);
        }

        list.ShouldBe([1, 1, 1, 1, 1, 1, 1, 1, 1, 1]);
    }

    [Test]
    public void Episodes_Should_Shuffle()
    {
        List<MediaItem> contents = Episodes(10);

        var state = new CollectionEnumeratorState();

        var groupedMediaItems = contents.Map(mi => new GroupedMediaItem(mi, null)).ToList();
        var shuffledContent = new ShuffledMediaCollectionEnumerator(groupedMediaItems, state, _cancellationToken);

        var list = new List<int>();
        for (var i = 1; i <= 10; i++)
        {
            shuffledContent.Current.IsSome.ShouldBeTrue();
            shuffledContent.Current.Do(x => list.Add(x.Id));
            shuffledContent.MoveNext(Option<DateTimeOffset>.None);
        }

        list.ShouldNotBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        list.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], true);
    }

    [Test]
    public void State_Index_Should_Increment()
    {
        List<MediaItem> contents = Episodes(10);
        var state = new CollectionEnumeratorState();

        var groupedMediaItems = contents.Map(mi => new GroupedMediaItem(mi, null)).ToList();
        var shuffledContent = new ShuffledMediaCollectionEnumerator(groupedMediaItems, state, _cancellationToken);

        for (var i = 0; i < 10; i++)
        {
            shuffledContent.State.Index.ShouldBe(i);
            shuffledContent.MoveNext(Option<DateTimeOffset>.None);
        }
    }

    [Test]
    public void State_Should_Impact_Iterator_Start()
    {
        List<MediaItem> contents = Episodes(10);
        var state = new CollectionEnumeratorState { Index = 5, Seed = MagicSeed };

        var groupedMediaItems = contents.Map(mi => new GroupedMediaItem(mi, null)).ToList();
        var shuffledContent = new ShuffledMediaCollectionEnumerator(groupedMediaItems, state, _cancellationToken);

        for (var i = 6; i <= 10; i++)
        {
            shuffledContent.Current.IsSome.ShouldBeTrue();
            shuffledContent.Current.Map(x => x.Id).IfNone(-1).ShouldBe(i);
            shuffledContent.State.Index.ShouldBe(i - 1);
            shuffledContent.MoveNext(Option<DateTimeOffset>.None);
        }
    }

    [Test]
    public void State_Should_Reset_When_Invalid()
    {
        List<MediaItem> contents = Episodes(10);
        var state = new CollectionEnumeratorState { Index = 10, Seed = MagicSeed };

        var groupedMediaItems = contents.Map(mi => new GroupedMediaItem(mi, null)).ToList();
        var shuffledContent = new ShuffledMediaCollectionEnumerator(groupedMediaItems, state, _cancellationToken);

        shuffledContent.State.Index.ShouldBe(0);
        shuffledContent.State.Seed.ShouldNotBe(MagicSeed);
    }

    /// <summary>
    ///     MoveNext walks (and wraps at) the flattened item count, so an index up to one less than
    ///     that is valid state. The constructor validates against the group count instead, so any
    ///     index at or above it is thrown away even though the enumerator itself produced it.
    /// </summary>
    [Test]
    public void State_Should_Not_Reset_When_Valid_For_Grouped_Items()
    {
        List<MediaItem> contents = Episodes(10);

        // 9 groups covering 10 items, so MoveNext wraps at 10 while the group count is 9
        var groupedMediaItems = contents.Take(8).Map(mi => new GroupedMediaItem(mi, null)).ToList();
        groupedMediaItems.Add(new GroupedMediaItem(contents[8], [contents[9]]));

        var state = new CollectionEnumeratorState { Index = 9, Seed = MagicSeed };

        var shuffledContent = new ShuffledMediaCollectionEnumerator(groupedMediaItems, state, _cancellationToken);

        shuffledContent.State.Index.ShouldBe(9);
        shuffledContent.State.Seed.ShouldBe(MagicSeed);
    }

    private static List<MediaItem> Episodes(int count) =>
        Range(1, count).Map(i => (MediaItem)new Episode
            {
                Id = i,
                EpisodeMetadata = new List<EpisodeMetadata>
                {
                    new()
                    {
                        ReleaseDate = new DateTime(2020, 1, i)
                    }
                }
            })
            .Reverse()
            .ToList();
}

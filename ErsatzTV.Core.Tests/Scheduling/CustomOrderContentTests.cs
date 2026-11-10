using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

public class CustomOrderContentTests
{
    private CancellationToken _cancellationToken;

    [SetUp]
    public void SetUp() => _cancellationToken = new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Test]
    public void MediaItems_Should_Sort_By_CustomOrder()
    {
        Collection collection = CreateCollection(10);
        List<MediaItem> contents = Episodes(10);
        var state = new CollectionEnumeratorState();

        var customOrderContent = new CustomOrderCollectionEnumerator(collection, contents, state);

        for (var i = 10; i >= 1; i--)
        {
            customOrderContent.Current.IsSome.ShouldBeTrue();
            customOrderContent.Current.Map(x => x.Id).IfNone(-1).ShouldBe(i);
            customOrderContent.MoveNext(Option<DateTimeOffset>.None);
        }
    }

    [Test]
    public void State_Index_Should_Increment()
    {
        Collection collection = CreateCollection(10);
        List<MediaItem> contents = Episodes(10);
        var state = new CollectionEnumeratorState();

        var customOrderContent = new CustomOrderCollectionEnumerator(collection, contents, state);

        for (var i = 0; i < 10; i++)
        {
            customOrderContent.State.Index.ShouldBe(i % 10);
            customOrderContent.MoveNext(Option<DateTimeOffset>.None);
        }
    }

    [Test]
    public void State_Should_Impact_Iterator_Start()
    {
        Collection collection = CreateCollection(10);
        List<MediaItem> contents = Episodes(10);
        var state = new CollectionEnumeratorState { Index = 5 };

        var customOrderContent = new CustomOrderCollectionEnumerator(collection, contents, state);

        for (var i = 5; i >= 1; i--)
        {
            customOrderContent.Current.IsSome.ShouldBeTrue();
            customOrderContent.Current.Map(x => x.Id).IfNone(-1).ShouldBe(i);
            customOrderContent.State.Index.ShouldBe(5 - i + 5); // 5 through 10
            customOrderContent.MoveNext(Option<DateTimeOffset>.None);
        }
    }

    [Test]
    public void State_Index_Past_The_End_Should_Start_At_The_Beginning()
    {
        Collection collection = CreateCollection(5);
        List<MediaItem> contents = Episodes(5);
        var state = new CollectionEnumeratorState { Index = 7 };

        var customOrderContent = new CustomOrderCollectionEnumerator(collection, contents, state);

        customOrderContent.State.Index.ShouldBe(0);
        customOrderContent.Current.Map(x => x.Id).IfNone(-1).ShouldBe(5);
    }

    [Test]
    public void Empty_Collection_Should_Not_Throw()
    {
        var collection = new Collection { CollectionItems = [] };
        var state = new CollectionEnumeratorState { Index = 3 };

        var customOrderContent = new CustomOrderCollectionEnumerator(collection, [], state);
        customOrderContent.MoveNext(Option<DateTimeOffset>.None);

        customOrderContent.Current.IsNone.ShouldBeTrue();
    }

    private static Collection CreateCollection(int episodeCount)
    {
        var collection = new Collection { CollectionItems = new List<CollectionItem>() };

        for (var i = 1; i <= episodeCount; i++)
        {
            collection.CollectionItems.Add(
                new CollectionItem
                {
                    MediaItemId = i,
                    // reverse order
                    CustomIndex = episodeCount - i
                });
        }

        return collection;
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

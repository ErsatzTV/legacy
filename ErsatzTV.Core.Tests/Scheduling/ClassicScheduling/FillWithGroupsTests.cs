using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Tests.Fakes;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Core.Tests.Scheduling.ClassicScheduling;

[TestFixture]
public class FillWithGroupsTests : PlayoutBuilderTestBase
{
    // mirrors MediaCollectionRepository, where movies and other videos are two key-only groups
    private static List<CollectionWithItems> TwoKeyOnlyGroups(List<MediaItem> items, string fakeKey) =>
    [
        new(-1, -1, fakeKey, items.Filter(i => i.Id <= 3).ToList(), true, PlaybackOrder.Chronological, false),
        new(-2, -2, $"{fakeKey}:other", items.Filter(i => i.Id > 3).ToList(), true, PlaybackOrder.Chronological, false)
    ];

    [Test]
    public async Task Two_Key_Only_Groups_Should_Alternate()
    {
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = TestData(TwoKeyOnlyGroups);

        DateTimeOffset start = HoursAfterMidnight(0);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            start + TimeSpan.FromHours(6),
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (PlayoutBuildResult result in buildResult.RightToSeq())
        {
            result.AddedItems.Map(i => i.MediaItemId).ShouldBe([1, 4, 2, 5, 3, 6]);
        }
    }

    [Test]
    public async Task Two_Key_Only_Groups_Should_Continue_From_Their_Own_Anchors()
    {
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = TestData(TwoKeyOnlyGroups);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(3);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (PlayoutBuildResult result in buildResult.RightToSeq())
        {
            result.AddedItems.Map(i => i.MediaItemId).ShouldBe([1, 4, 2]);
        }

        Either<BaseError, PlayoutBuildResult> buildResult2 = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            finish,
            finish + TimeSpan.FromHours(3),
            CancellationToken);

        buildResult2.IsRight.ShouldBeTrue();
        foreach (PlayoutBuildResult result in buildResult2.RightToSeq())
        {
            result.AddedItems.Map(i => i.MediaItemId).ShouldBe([5, 3, 6]);
        }
    }

    [Test]
    public async Task No_Groups_Should_Schedule_The_Collection()
    {
        // images and remote streams give no groups
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = TestData((_, _) => []);

        DateTimeOffset start = HoursAfterMidnight(0);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            start + TimeSpan.FromHours(6),
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (PlayoutBuildResult result in buildResult.RightToSeq())
        {
            result.AddedItems.Map(i => i.MediaItemId).ShouldBe([1, 2, 3, 4, 5, 6]);
        }

        playout.FillGroupIndices.ShouldBeEmpty();
    }

    private TestData TestData(Func<List<MediaItem>, string, List<CollectionWithItems>> grouper)
    {
        var collection = new Collection
        {
            Id = 1,
            Name = "Grouped Items",
            MediaItems = Range(1, 6)
                .Map(i => (MediaItem)TestMovie(i, TimeSpan.FromHours(1), new DateTime(2020, 1, i)))
                .ToList()
        };

        var fakeRepository = new FakeMediaCollectionRepository(Map((collection.Id, collection.MediaItems.ToList())))
        {
            FakeCollectionGrouper = grouper
        };

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemMultiple
            {
                Id = 1,
                Index = 1,
                CollectionType = CollectionType.Collection,
                CollectionId = collection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological,
                MultipleMode = MultipleMode.Count,
                Count = "1",
                FillWithGroupMode = FillWithGroupMode.FillWithOrderedGroups,
                ProgramScheduleItemGraphicsElements = []
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule { Items = items },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData = new PlayoutReferenceData(
            playout.Channel,
            Option<Deco>.None,
            [],
            [],
            playout.ProgramSchedule,
            [],
            [],
            TimeSpan.Zero);

        var builder = new PlayoutBuilder(
            Substitute.For<IConfigElementRepository>(),
            fakeRepository,
            new FakeTelevisionRepository(),
            Substitute.For<IArtistRepository>(),
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>(),
            new MockFileSystem(),
            Substitute.For<IRerunHelper>(),
            Logger);

        return new TestData(builder, playout, referenceData);
    }
}

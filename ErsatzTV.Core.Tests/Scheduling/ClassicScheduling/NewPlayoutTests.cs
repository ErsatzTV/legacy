using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
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
public class NewPlayoutTests : PlayoutBuilderTestBase
{
    [Test]
    public async Task FillWithGroupMode_Should_Not_Fail()
    {
        var collection = new Collection
        {
            Id = 1,
            Name = "Multiple Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map((collection.Id, collection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemMultiple
            {
                Id = 1,
                Index = 1,
                //Collection = collection,
                CollectionId = collection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological,
                MultipleMode = MultipleMode.Count,
                Count = "2",
                FillWithGroupMode = FillWithGroupMode.FillWithShuffledGroups,
                ProgramScheduleItemGraphicsElements = []
            }
        };

        // having a graphics element reference the schedule item triggers the bug
        items[0].ProgramScheduleItemGraphicsElements.Add(new ProgramScheduleItemGraphicsElement
        {
            GraphicsElementId = 1,
            ProgramScheduleItem = items[0],
            ProgramScheduleItemId = items[0].Id
        });

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
    }

    [Test]
    public async Task OnlyZeroDurationItem_Should_Abort()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.Zero, DateTime.Today)
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random);

        Either<BaseError, PlayoutBuildResult> buildResult =
            await builder.Build(DateTimeOffset.Now, playout, referenceData, PlayoutBuildMode.Reset, CancellationToken);

        buildResult.IsLeft.ShouldBeTrue();
    }

    [Test]
    public async Task ZeroDurationItem_Should_BeSkipped()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.Zero, DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(6), DateTime.Today)
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().MediaItemId.ShouldBe(2);
            result.AddedItems.Head().StartOffset.ShouldBe(start);
            result.AddedItems.Head().FinishOffset.ShouldBe(start + TimeSpan.FromHours(6));
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(6));
    }

    [Test]
    public async Task OnlyFileNotFoundItem_Should_Abort()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(1), DateTime.Today)
        };

        mediaItems[0].State = MediaItemState.FileNotFound;

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<bool>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutSkipMissingItems.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(true));

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random, configRepo);

        Either<BaseError, PlayoutBuildResult> buildResult =
            await builder.Build(DateTimeOffset.Now, playout, referenceData, PlayoutBuildMode.Reset, CancellationToken);

        buildResult.IsLeft.ShouldBeTrue();
    }

    [Test]
    public async Task FileNotFoundItem_Should_BeSkipped()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(1), DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(6), DateTime.Today)
        };

        mediaItems[0].State = MediaItemState.FileNotFound;

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<bool>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutSkipMissingItems.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(true));

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random, configRepo);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().MediaItemId.ShouldBe(2);
            result.AddedItems.Head().StartOffset.ShouldBe(start);
            result.AddedItems.Head().FinishOffset.ShouldBe(finish);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task OnlyUnavailableItem_Should_Abort()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(1), DateTime.Today)
        };

        mediaItems[0].State = MediaItemState.Unavailable;

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<bool>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutSkipMissingItems.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(true));

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random, configRepo);

        Either<BaseError, PlayoutBuildResult> buildResult =
            await builder.Build(DateTimeOffset.Now, playout, referenceData, PlayoutBuildMode.Reset, CancellationToken);

        buildResult.IsLeft.ShouldBeTrue();
    }

    [Test]
    public async Task UnavailableItem_Should_BeSkipped()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(1), DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(6), DateTime.Today)
        };

        mediaItems[0].State = MediaItemState.Unavailable;

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<bool>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutSkipMissingItems.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(true));

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random, configRepo);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().MediaItemId.ShouldBe(2);
            result.AddedItems.Head().StartOffset.ShouldBe(start);
            result.AddedItems.Head().FinishOffset.ShouldBe(finish);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task FileNotFound_Should_NotBeSkippedIfConfigured()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), DateTime.Today)
        };

        mediaItems[0].State = MediaItemState.FileNotFound;

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<bool>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutSkipMissingItems.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(false));

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random, configRepo);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().StartOffset.ShouldBe(start);
            result.AddedItems.Head().FinishOffset.ShouldBe(finish);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task Unavailable_Should_NotBeSkippedIfConfigured()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), DateTime.Today)
        };

        mediaItems[0].State = MediaItemState.Unavailable;

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        configRepo
            .GetValue<bool>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutSkipMissingItems.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(false));

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random, configRepo);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().StartOffset.ShouldBe(start);
            result.AddedItems.Head().FinishOffset.ShouldBe(finish);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task InitialFlood_Should_StartAtMidnight()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), DateTime.Today)
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().StartOffset.ShouldBe(start);
            result.AddedItems.Head().FinishOffset.ShouldBe(finish);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task InitialFlood_Should_StartAtMidnight_With_LateStart()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), DateTime.Today)
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Random);
        DateTimeOffset midnight = HoursAfterMidnight(0);
        DateTimeOffset start = HoursAfterMidnight(1);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(2);
            result.AddedItems[0].StartOffset.ShouldBe(midnight);
            result.AddedItems[1].StartOffset.ShouldBe(midnight + TimeSpan.FromHours(6));
            result.AddedItems[1].FinishOffset.ShouldBe(midnight + TimeSpan.FromHours(12));
        }

        playout.Anchor.NextStartOffset.ShouldBe(midnight + TimeSpan.FromHours(12));
    }

    [Test]
    public async Task ChronologicalContent_Should_CreateChronologicalItems()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
            TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Chronological);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(4);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(4);
            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(1);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[3].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(4));
    }

    [Test]
    public async Task ChronologicalFlood_Should_AnchorAndMaintainExistingPlayout()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(6), DateTime.Today.AddHours(1))
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Chronological);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().MediaItemId.ShouldBe(1);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);

        playout.ProgramScheduleAnchors.Count.ShouldBe(1);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(1);

        DateTimeOffset start2 = HoursAfterMidnight(1);
        DateTimeOffset finish2 = start2 + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult2 = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start2,
            finish2,
            CancellationToken);

        buildResult2.IsRight.ShouldBeTrue();
        foreach (var result2 in buildResult2.RightToSeq())
        {
            result2.AddedItems.Count.ShouldBe(1);
            result2.AddedItems[0].StartOffset.ShouldBe(finish);
            result2.AddedItems[0].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(12));
        playout.ProgramScheduleAnchors.Count.ShouldBe(1);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(0);
    }

    [Test]
    public async Task ChronologicalFlood_Should_AnchorAndReturnNewPlayoutItems()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(6), DateTime.Today.AddHours(1))
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Chronological);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems.Head().MediaItemId.ShouldBe(1);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
        playout.ProgramScheduleAnchors.Count.ShouldBe(1);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(1);

        DateTimeOffset start2 = HoursAfterMidnight(1);
        DateTimeOffset finish2 = start2 + TimeSpan.FromHours(12);

        Either<BaseError, PlayoutBuildResult> buildResult2 = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start2,
            finish2,
            CancellationToken);

        buildResult2.IsRight.ShouldBeTrue();
        foreach (var result2 in buildResult2.RightToSeq())
        {
            result2.AddedItems.Count.ShouldBe(2);
            result2.AddedItems[0].StartOffset.ShouldBe(finish);
            result2.AddedItems[0].MediaItemId.ShouldBe(2);
            result2.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(12));
            result2.AddedItems[1].MediaItemId.ShouldBe(1);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(18));
        playout.ProgramScheduleAnchors.Count.ShouldBe(1);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(1);
    }

    [Test]
    public async Task FloodContent_Should_FloodAroundFixedContent_One()
    {
        var floodCollection = new Collection
        {
            Id = 1,
            Name = "Flood Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var fixedCollection = new Collection
        {
            Id = 2,
            Name = "Fixed Items",
            MediaItems = [TestMovie(3, TimeSpan.FromHours(2), new DateTime(2020, 1, 1))]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (floodCollection.Id, floodCollection.MediaItems.ToList()),
                (fixedCollection.Id, fixedCollection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemFlood
            {
                Id = 1,
                Index = 1,
                Collection = floodCollection,
                CollectionId = floodCollection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemOne
            {
                Id = 2,
                Index = 2,
                Collection = fixedCollection,
                CollectionId = fixedCollection.Id,
                StartTime = TimeSpan.FromHours(3),
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(5);
            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(1);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[3].MediaItemId.ShouldBe(3);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(5));
            result.AddedItems[4].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task FloodContent_Should_FloodAroundFixedContent_One_Multiple_Days()
    {
        var floodCollection = new Collection
        {
            Id = 1,
            Name = "Flood Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var fixedCollection = new Collection
        {
            Id = 2,
            Name = "Fixed Items",
            MediaItems = [TestMovie(3, TimeSpan.FromHours(2), new DateTime(2020, 1, 1))]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (floodCollection.Id, floodCollection.MediaItems.ToList()),
                (fixedCollection.Id, fixedCollection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemFlood
            {
                Id = 1,
                Index = 1,
                Collection = floodCollection,
                CollectionId = floodCollection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemOne
            {
                Id = 2,
                Index = 2,
                Collection = fixedCollection,
                CollectionId = fixedCollection.Id,
                StartTime = TimeSpan.FromHours(3),
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(30);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(28);
            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(1);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[3].MediaItemId.ShouldBe(3);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(5));
            result.AddedItems[4].MediaItemId.ShouldBe(2);
            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(6));
            result.AddedItems[5].MediaItemId.ShouldBe(1);
            result.AddedItems[6].StartOffset.ShouldBe(start + TimeSpan.FromHours(7));
            result.AddedItems[6].MediaItemId.ShouldBe(2);
            result.AddedItems[7].StartOffset.ShouldBe(start + TimeSpan.FromHours(8));
            result.AddedItems[7].MediaItemId.ShouldBe(1);
            result.AddedItems[8].StartOffset.ShouldBe(start + TimeSpan.FromHours(9));
            result.AddedItems[8].MediaItemId.ShouldBe(2);
            result.AddedItems[9].StartOffset.ShouldBe(start + TimeSpan.FromHours(10));
            result.AddedItems[9].MediaItemId.ShouldBe(1);
            result.AddedItems[10].StartOffset.ShouldBe(start + TimeSpan.FromHours(11));
            result.AddedItems[10].MediaItemId.ShouldBe(2);
            result.AddedItems[11].StartOffset.ShouldBe(start + TimeSpan.FromHours(12));
            result.AddedItems[11].MediaItemId.ShouldBe(1);
            result.AddedItems[12].StartOffset.ShouldBe(start + TimeSpan.FromHours(13));
            result.AddedItems[12].MediaItemId.ShouldBe(2);
            result.AddedItems[13].StartOffset.ShouldBe(start + TimeSpan.FromHours(14));
            result.AddedItems[13].MediaItemId.ShouldBe(1);
            result.AddedItems[14].StartOffset.ShouldBe(start + TimeSpan.FromHours(15));
            result.AddedItems[14].MediaItemId.ShouldBe(2);
            result.AddedItems[15].StartOffset.ShouldBe(start + TimeSpan.FromHours(16));
            result.AddedItems[15].MediaItemId.ShouldBe(1);
            result.AddedItems[16].StartOffset.ShouldBe(start + TimeSpan.FromHours(17));
            result.AddedItems[16].MediaItemId.ShouldBe(2);
            result.AddedItems[17].StartOffset.ShouldBe(start + TimeSpan.FromHours(18));
            result.AddedItems[17].MediaItemId.ShouldBe(1);
            result.AddedItems[18].StartOffset.ShouldBe(start + TimeSpan.FromHours(19));
            result.AddedItems[18].MediaItemId.ShouldBe(2);
            result.AddedItems[19].StartOffset.ShouldBe(start + TimeSpan.FromHours(20));
            result.AddedItems[19].MediaItemId.ShouldBe(1);
            result.AddedItems[20].StartOffset.ShouldBe(start + TimeSpan.FromHours(21));
            result.AddedItems[20].MediaItemId.ShouldBe(2);
            result.AddedItems[21].StartOffset.ShouldBe(start + TimeSpan.FromHours(22));
            result.AddedItems[21].MediaItemId.ShouldBe(1);
            result.AddedItems[22].StartOffset.ShouldBe(start + TimeSpan.FromHours(23));
            result.AddedItems[22].MediaItemId.ShouldBe(2);
            result.AddedItems[23].StartOffset.ShouldBe(start + TimeSpan.FromHours(24));
            result.AddedItems[23].MediaItemId.ShouldBe(1);
            result.AddedItems[24].StartOffset.ShouldBe(start + TimeSpan.FromHours(25));
            result.AddedItems[24].MediaItemId.ShouldBe(2);
            result.AddedItems[25].StartOffset.ShouldBe(start + TimeSpan.FromHours(26));
            result.AddedItems[25].MediaItemId.ShouldBe(1);
            result.AddedItems[26].StartOffset.ShouldBe(start + TimeSpan.FromHours(27));
            result.AddedItems[26].MediaItemId.ShouldBe(3);
            result.AddedItems[27].StartOffset.ShouldBe(start + TimeSpan.FromHours(29));
            result.AddedItems[27].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(30));
    }

    [Test]
    public async Task FloodContent_Should_FloodAroundFixedContent_Multiple()
    {
        var floodCollection = new Collection
        {
            Id = 1,
            Name = "Flood Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var fixedCollection = new Collection
        {
            Id = 2,
            Name = "Fixed Items",
            MediaItems =
            [
                TestMovie(3, TimeSpan.FromHours(2), new DateTime(2020, 1, 1)),
                TestMovie(4, TimeSpan.FromHours(1), new DateTime(2020, 1, 2))
            ]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (floodCollection.Id, floodCollection.MediaItems.ToList()),
                (fixedCollection.Id, fixedCollection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemFlood
            {
                Id = 1,
                Index = 1,
                Collection = floodCollection,
                CollectionId = floodCollection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemMultiple
            {
                Id = 2,
                Index = 2,
                Collection = fixedCollection,
                CollectionId = fixedCollection.Id,
                StartTime = TimeSpan.FromHours(3),
                Count = "2",
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(7);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(6);

            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(1);

            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[3].MediaItemId.ShouldBe(3);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(5));
            result.AddedItems[4].MediaItemId.ShouldBe(4);

            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(6));
            result.AddedItems[5].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task FloodContent_Should_FloodAroundFixedContent_Multiple_With_Gap()
    {
        var floodCollection = new Collection
        {
            Id = 1,
            Name = "Flood Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromMinutes(50), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var fixedCollection = new Collection
        {
            Id = 2,
            Name = "Fixed Items",
            MediaItems =
            [
                TestMovie(3, TimeSpan.FromHours(2), new DateTime(2020, 1, 1)),
                TestMovie(4, TimeSpan.FromHours(1), new DateTime(2020, 1, 2))
            ]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (floodCollection.Id, floodCollection.MediaItems.ToList()),
                (fixedCollection.Id, fixedCollection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemFlood
            {
                Id = 1,
                Index = 1,
                Collection = floodCollection,
                CollectionId = floodCollection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemMultiple
            {
                Id = 2,
                Index = 2,
                Collection = fixedCollection,
                CollectionId = fixedCollection.Id,
                StartTime = TimeSpan.FromHours(3),
                Count = "2",
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(7);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(6);

            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromMinutes(50));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromMinutes(50 + 60));
            result.AddedItems[2].MediaItemId.ShouldBe(1);

            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[3].MediaItemId.ShouldBe(3);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(5));
            result.AddedItems[4].MediaItemId.ShouldBe(4);

            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(6));
            result.AddedItems[5].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task FloodContent_Should_FloodWithFixedStartTime()
    {
        var floodCollection = new Collection
        {
            Id = 1,
            Name = "Flood Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var fixedCollection = new Collection
        {
            Id = 2,
            Name = "Fixed Items",
            MediaItems =
            [
                TestMovie(3, TimeSpan.FromHours(2), new DateTime(2020, 1, 1)),
                TestMovie(4, TimeSpan.FromHours(1), new DateTime(2020, 1, 2))
            ]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (floodCollection.Id, floodCollection.MediaItems.ToList()),
                (fixedCollection.Id, fixedCollection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemFlood
            {
                Id = 1,
                Index = 1,
                Collection = floodCollection,
                CollectionId = floodCollection.Id,
                StartTime = TimeSpan.FromHours(7),
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemOne
            {
                Id = 2,
                Index = 2,
                Collection = fixedCollection,
                CollectionId = fixedCollection.Id,
                StartTime = TimeSpan.FromHours(12),
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(24);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(6);

            result.AddedItems[0].StartOffset.ShouldBe(start + TimeSpan.FromHours(7));
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(8));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(9));
            result.AddedItems[2].MediaItemId.ShouldBe(1);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(10));
            result.AddedItems[3].MediaItemId.ShouldBe(2);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(11));
            result.AddedItems[4].MediaItemId.ShouldBe(1);

            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(12));
            result.AddedItems[5].MediaItemId.ShouldBe(3);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(31));
    }

    [Test]
    public async Task FloodContent_Should_FloodAroundFixedContent_DurationWithoutOfflineTail()
    {
        var floodCollection = new Collection
        {
            Id = 1,
            Name = "Flood Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var fixedCollection = new Collection
        {
            Id = 2,
            Name = "Fixed Items",
            MediaItems =
            [
                TestMovie(3, TimeSpan.FromHours(0.75), new DateTime(2020, 1, 1)),
                TestMovie(4, TimeSpan.FromHours(1.5), new DateTime(2020, 1, 2))
            ]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (floodCollection.Id, floodCollection.MediaItems.ToList()),
                (fixedCollection.Id, fixedCollection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemFlood
            {
                Id = 1,
                Index = 1,
                Collection = floodCollection,
                CollectionId = floodCollection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemDuration
            {
                Id = 2,
                Index = 2,
                Collection = fixedCollection,
                CollectionId = fixedCollection.Id,
                StartTime = TimeSpan.FromHours(2),
                PlayoutDuration = TimeSpan.FromHours(2),
                TailMode = TailMode.None, // immediately continue
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(7);

            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(2);

            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(3);

            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(2.75));
            result.AddedItems[3].MediaItemId.ShouldBe(1);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(3.75));
            result.AddedItems[4].MediaItemId.ShouldBe(2);

            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(4.75));
            result.AddedItems[5].MediaItemId.ShouldBe(1);
            result.AddedItems[6].StartOffset.ShouldBe(start + TimeSpan.FromHours(5.75));
            result.AddedItems[6].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(6.75));
    }

    [Test]
    public async Task MultipleContent_Should_WrapAroundDynamicContent_DurationWithoutOfflineTail()
    {
        var multipleCollection = new Collection
        {
            Id = 1,
            Name = "Multiple Items",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 2, 1))
            ]
        };

        var dynamicCollection = new Collection
        {
            Id = 2,
            Name = "Dynamic Items",
            MediaItems =
            [
                TestMovie(3, TimeSpan.FromHours(0.75), new DateTime(2020, 1, 1)),
                TestMovie(4, TimeSpan.FromHours(1.5), new DateTime(2020, 1, 2))
            ]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (multipleCollection.Id, multipleCollection.MediaItems.ToList()),
                (dynamicCollection.Id, dynamicCollection.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemMultiple
            {
                Id = 1,
                Index = 1,
                Collection = multipleCollection,
                CollectionId = multipleCollection.Id,
                StartTime = null,
                Count = "2",
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemDuration
            {
                Id = 2,
                Index = 2,
                Collection = dynamicCollection,
                CollectionId = dynamicCollection.Id,
                StartTime = null,
                PlayoutDuration = TimeSpan.FromHours(2),
                TailMode = TailMode.None, // immediately continue
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(6);

            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(2);

            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(3);

            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(2.75));
            result.AddedItems[3].MediaItemId.ShouldBe(1);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(3.75));
            result.AddedItems[4].MediaItemId.ShouldBe(2);

            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(4.75));
            result.AddedItems[5].MediaItemId.ShouldBe(4);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(6.25));
    }

    [Test]
    public async Task Multiple_Mode_Collection_Size()
    {
        var collectionOne = new Collection
        {
            Id = 1,
            Name = "Multiple Items 1",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(3, TimeSpan.FromHours(1), new DateTime(2020, 1, 1))
            ]
        };

        var collectionTwo = new Collection
        {
            Id = 2,
            Name = "Multiple Items 2",
            MediaItems =
            [
                TestMovie(4, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(5, TimeSpan.FromHours(1), new DateTime(2020, 1, 1))
            ]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (collectionOne.Id, collectionOne.MediaItems.ToList()),
                (collectionTwo.Id, collectionTwo.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemMultiple
            {
                Id = 1,
                Index = 1,
                Collection = collectionOne,
                CollectionId = collectionOne.Id,
                StartTime = null,
                Count = "0",
                MultipleMode = MultipleMode.CollectionSize,
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemMultiple
            {
                Id = 2,
                Index = 2,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id,
                StartTime = null,
                Count = "0",
                MultipleMode = MultipleMode.CollectionSize,
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(5);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(5);

            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(3);

            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[3].MediaItemId.ShouldBe(4);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(4));
            result.AddedItems[4].MediaItemId.ShouldBe(5);
        }

        playout.Anchor.ScheduleItemsEnumeratorState.Index.ShouldBe(0);
        playout.Anchor.MultipleRemaining.ShouldBeNull();
        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task Alternating_Duration_With_Filler_Should_Alternate_Schedule_Items()
    {
        var collectionOne = new Collection
        {
            Id = 1,
            Name = "Duration Items 1",
            MediaItems = [TestMovie(1, TimeSpan.FromMinutes(55), new DateTime(2020, 1, 1))]
        };

        var collectionTwo = new Collection
        {
            Id = 2,
            Name = "Duration Items 2",
            MediaItems = [TestMovie(2, TimeSpan.FromMinutes(55), new DateTime(2020, 1, 1))]
        };

        var collectionThree = new Collection
        {
            Id = 3,
            Name = "Filler Items",
            MediaItems = [TestMovie(3, TimeSpan.FromMinutes(5), new DateTime(2020, 1, 1))]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (collectionOne.Id, collectionOne.MediaItems.ToList()),
                (collectionTwo.Id, collectionTwo.MediaItems.ToList()),
                (collectionThree.Id, collectionThree.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemDuration
            {
                Id = 1,
                Index = 1,
                Collection = collectionOne,
                CollectionId = collectionOne.Id,
                StartTime = null,
                PlayoutDuration = TimeSpan.FromHours(3),
                PlaybackOrder = PlaybackOrder.Chronological,
                TailMode = TailMode.Filler,
                TailFiller = new FillerPreset
                {
                    FillerKind = FillerKind.Tail,
                    Collection = collectionThree,
                    CollectionId = collectionThree.Id
                }
            },
            new ProgramScheduleItemDuration
            {
                Id = 2,
                Index = 2,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id,
                StartTime = null,
                PlayoutDuration = TimeSpan.FromHours(3),
                PlaybackOrder = PlaybackOrder.Chronological,
                TailMode = TailMode.Filler,
                TailFiller = new FillerPreset
                {
                    FillerKind = FillerKind.Tail,
                    Collection = collectionThree,
                    CollectionId = collectionThree.Id
                }
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(12);

            result.AddedItems[0].StartOffset.ShouldBe(start + TimeSpan.FromMinutes(0));
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromMinutes(55));
            result.AddedItems[1].MediaItemId.ShouldBe(1);
            result.AddedItems[2].StartOffset.ShouldBe(start + new TimeSpan(1, 50, 0));
            result.AddedItems[2].MediaItemId.ShouldBe(1);

            result.AddedItems[3].StartOffset.ShouldBe(start + new TimeSpan(2, 45, 0));
            result.AddedItems[3].MediaItemId.ShouldBe(3);
            result.AddedItems[4].StartOffset.ShouldBe(start + new TimeSpan(2, 50, 0));
            result.AddedItems[4].MediaItemId.ShouldBe(3);
            result.AddedItems[5].StartOffset.ShouldBe(start + new TimeSpan(2, 55, 0));
            result.AddedItems[5].MediaItemId.ShouldBe(3);

            result.AddedItems[6].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[6].MediaItemId.ShouldBe(2);
            result.AddedItems[7].StartOffset.ShouldBe(start + new TimeSpan(3, 55, 0));
            result.AddedItems[7].MediaItemId.ShouldBe(2);
            result.AddedItems[8].StartOffset.ShouldBe(start + new TimeSpan(4, 50, 0));
            result.AddedItems[8].MediaItemId.ShouldBe(2);

            result.AddedItems[9].StartOffset.ShouldBe(start + new TimeSpan(5, 45, 0));
            result.AddedItems[9].MediaItemId.ShouldBe(3);
            result.AddedItems[10].StartOffset.ShouldBe(start + new TimeSpan(5, 50, 0));
            result.AddedItems[10].MediaItemId.ShouldBe(3);
            result.AddedItems[11].StartOffset.ShouldBe(start + new TimeSpan(5, 55, 0));
            result.AddedItems[11].MediaItemId.ShouldBe(3);
        }

        playout.Anchor.ScheduleItemsEnumeratorState.Index.ShouldBe(0);
        playout.Anchor.DurationFinish.ShouldBeNull();

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(6));
    }

    [Test]
    public async Task Multiple_With_Filler_Should_Keep_Filler_After_End_Of_Playout()
    {
        var collectionOne = new Collection
        {
            Id = 1,
            Name = "Duration Items 1",
            MediaItems = [TestMovie(1, TimeSpan.FromMinutes(61), new DateTime(2020, 1, 1))]
        };

        var collectionTwo = new Collection
        {
            Id = 2,
            Name = "Filler Items",
            MediaItems = [TestMovie(2, TimeSpan.FromMinutes(4), new DateTime(2020, 1, 1))]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (collectionOne.Id, collectionOne.MediaItems.ToList()),
                (collectionTwo.Id, collectionTwo.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemMultiple
            {
                Id = 1,
                Index = 1,
                Collection = collectionOne,
                CollectionId = collectionOne.Id,
                StartTime = null,
                Count = "1",
                PlaybackOrder = PlaybackOrder.Chronological,
                PostRollFiller = new FillerPreset
                {
                    FillerKind = FillerKind.PostRoll,
                    Collection = collectionTwo,
                    CollectionId = collectionTwo.Id,
                    FillerMode = FillerMode.Count,
                    Count = 1
                }
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(1);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(2);

            result.AddedItems[0].StartOffset.ShouldBe(start + TimeSpan.FromMinutes(0));
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromMinutes(61));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.ScheduleItemsEnumeratorState.Index.ShouldBe(0);
        playout.Anchor.DurationFinish.ShouldBeNull();
        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromMinutes(65));
    }

    [Test]
    public async Task Duration_Should_Skip_Items_That_Are_Too_Long()
    {
        var collectionOne = new Collection
        {
            Id = 1,
            Name = "Duration Items 1",
            MediaItems =
            [
                TestMovie(1, TimeSpan.FromHours(2), new DateTime(2020, 1, 1)),
                TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 1, 1)),
                TestMovie(3, TimeSpan.FromHours(2), new DateTime(2020, 1, 1)),
                TestMovie(4, TimeSpan.FromHours(1), new DateTime(2020, 1, 1))
            ]
        };

        var fakeRepository =
            new FakeMediaCollectionRepository(Map((collectionOne.Id, collectionOne.MediaItems.ToList())));

        var items = new List<ProgramScheduleItem>
        {
            new ProgramScheduleItemDuration
            {
                Id = 1,
                Index = 1,
                Collection = collectionOne,
                CollectionId = collectionOne.Id,
                StartTime = null,
                PlayoutDuration = TimeSpan.FromHours(1),
                PlaybackOrder = PlaybackOrder.Chronological,
                TailMode = TailMode.None
            }
        };

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(
                playout.Channel,
                Option<Deco>.None,
                [],
                [],
                playout.ProgramSchedule,
                [],
                [],
                TimeSpan.Zero);

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();
        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();
        var builder = new PlayoutBuilder(
            configRepo,
            fakeRepository,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(6);

            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].MediaItemId.ShouldBe(2);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[1].MediaItemId.ShouldBe(4);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[2].MediaItemId.ShouldBe(2);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[3].MediaItemId.ShouldBe(4);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(4));
            result.AddedItems[4].MediaItemId.ShouldBe(2);
            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(5));
            result.AddedItems[5].MediaItemId.ShouldBe(4);
        }

        playout.Anchor.ScheduleItemsEnumeratorState.Index.ShouldBe(0);
        playout.Anchor.DurationFinish.ShouldBeNull();
        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task Two_Day_Playout_Should_Create_Date_Anchors_For_Midnight()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), new DateTime(2002, 1, 1)),
            TestMovie(2, TimeSpan.FromHours(6), new DateTime(2003, 1, 1)),
            TestMovie(3, TimeSpan.FromHours(6), new DateTime(2004, 1, 1))
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Chronological);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromDays(2);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Reset,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(8);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[0].StartOffset.ShouldBe(start);
            result.AddedItems[0].FinishOffset.ShouldBe(start + TimeSpan.FromHours(6));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(6));
            result.AddedItems[1].FinishOffset.ShouldBe(start + TimeSpan.FromHours(12));
            result.AddedItems[2].MediaItemId.ShouldBe(3);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(12));
            result.AddedItems[2].FinishOffset.ShouldBe(start + TimeSpan.FromHours(18));
            result.AddedItems[3].MediaItemId.ShouldBe(1);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(18));
            result.AddedItems[3].FinishOffset.ShouldBe(start + TimeSpan.FromHours(24));
            result.AddedItems[4].MediaItemId.ShouldBe(2);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(24));
            result.AddedItems[4].FinishOffset.ShouldBe(start + TimeSpan.FromHours(30));
            result.AddedItems[5].MediaItemId.ShouldBe(3);
            result.AddedItems[5].StartOffset.ShouldBe(start + TimeSpan.FromHours(30));
            result.AddedItems[5].FinishOffset.ShouldBe(start + TimeSpan.FromHours(36));
            result.AddedItems[6].MediaItemId.ShouldBe(1);
            result.AddedItems[6].StartOffset.ShouldBe(start + TimeSpan.FromHours(36));
            result.AddedItems[6].FinishOffset.ShouldBe(start + TimeSpan.FromHours(42));
            result.AddedItems[7].MediaItemId.ShouldBe(2);
            result.AddedItems[7].StartOffset.ShouldBe(start + TimeSpan.FromHours(42));
            result.AddedItems[7].FinishOffset.ShouldBe(start + TimeSpan.FromHours(48));
        }

        // a checkpoint for each midnight the two day build crossed, plus the continue anchor
        var checkpoints = playout.ProgramScheduleAnchors.Filter(a => a.AnchorDate is not null).ToList();
        checkpoints.Count.ShouldBe(2);
        checkpoints.Count(a => a.EnumeratorState.Index == 4 % 3).ShouldBe(1);
        checkpoints.Count(a => a.EnumeratorState.Index == 8 % 3).ShouldBe(1);

        PlayoutProgramScheduleAnchor continueAnchor =
            playout.ProgramScheduleAnchors.Single(a => a.AnchorDate is null);
        continueAnchor.EnumeratorState.Index.ShouldBe(8 % 3);

        int seed = playout.ProgramScheduleAnchors[0].EnumeratorState.Seed;
        playout.ProgramScheduleAnchors.All(a => a.EnumeratorState.Seed == seed).ShouldBeTrue();

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(48));
    }
}

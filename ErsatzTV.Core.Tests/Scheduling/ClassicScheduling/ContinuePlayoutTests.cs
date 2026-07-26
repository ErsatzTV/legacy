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
public class ContinuePlayoutTests : PlayoutBuilderTestBase
{
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
            result2.AddedItems[0].StartOffset.ShouldBe(start + TimeSpan.FromHours(6));
            result2.AddedItems[0].MediaItemId.ShouldBe(2);
            result2.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(12));
            result2.AddedItems[1].MediaItemId.ShouldBe(1);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(18));
        playout.ProgramScheduleAnchors.Count.ShouldBe(1);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(1);
    }

    [Test]
    public async Task ChronologicalFlood_Should_AnchorAndReturnNewPlayoutItems_MultiDay()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(6), DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(6), DateTime.Today.AddHours(1))
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Chronological);
        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromDays(1);

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
            result.AddedItems.Map(i => i.MediaItemId).ToList().ShouldBe([1, 2, 1, 2]);
        }

        playout.Anchor.NextStartOffset.ShouldBe(finish);

        // the continue anchor, plus a checkpoint for the midnight this build ended on
        playout.ProgramScheduleAnchors.Count.ShouldBe(2);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(0);

        PlayoutProgramScheduleAnchor headAnchor = playout.ProgramScheduleAnchors.Head();

        // throw in a detractor anchor - playout builder should prioritize the "continue" anchor
        playout.ProgramScheduleAnchors.Insert(
            0,
            new PlayoutProgramScheduleAnchor
            {
                Id = headAnchor.Id + 1,
                Collection = headAnchor.Collection,
                CollectionId = headAnchor.CollectionId,
                Playout = playout,
                PlayoutId = playout.Id,
                AnchorDate = DateTime.Today.ToUniversalTime(),
                CollectionType = headAnchor.CollectionType,
                EnumeratorState = new CollectionEnumeratorState
                    { Index = headAnchor.EnumeratorState.Index + 1, Seed = headAnchor.EnumeratorState.Seed },
                MediaItem = headAnchor.MediaItem,
                MediaItemId = headAnchor.MediaItemId,
                MultiCollection = headAnchor.MultiCollection,
                MultiCollectionId = headAnchor.MultiCollectionId,
                SmartCollection = headAnchor.SmartCollection,
                SmartCollectionId = headAnchor.SmartCollectionId
            });

        // continue 1h later
        DateTimeOffset start2 = HoursAfterMidnight(1);
        DateTimeOffset finish2 = start2 + TimeSpan.FromDays(1);

        buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start2,
            finish2,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(1);
            result.AddedItems[0].StartOffset.ShouldBe(finish);
            result.AddedItems[0].MediaItemId.ShouldBe(1);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(30));

        // continue anchor, detractor checkpoint, and the checkpoint for the midnight the first build crossed
        playout.ProgramScheduleAnchors.Count.ShouldBe(3);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(1);

        // continue 1h later
        DateTimeOffset start3 = HoursAfterMidnight(2);
        DateTimeOffset finish3 = start3 + TimeSpan.FromDays(1);

        buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start3,
            finish3,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(0);
        }

        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(30));

        // continue anchor, detractor checkpoint, and the checkpoint for the midnight the first build crossed
        playout.ProgramScheduleAnchors.Count.ShouldBe(3);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Index.ShouldBe(1);
    }

    [Test]
    public async Task ShuffleFlood_Should_MaintainRandomSeed()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(1), DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(1), DateTime.Today.AddHours(1)),
            TestMovie(3, TimeSpan.FromHours(1), DateTime.Today.AddHours(3))
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Shuffle);
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
        }

        playout.ProgramScheduleAnchors.Count.ShouldBe(1);
        playout.ProgramScheduleAnchors.Head().EnumeratorState.Seed.ShouldBeGreaterThan(0);

        int firstSeedValue = playout.ProgramScheduleAnchors.Head().EnumeratorState.Seed;

        playout.Anchor.NextStartOffset.ShouldBe(finish);

        DateTimeOffset start2 = HoursAfterMidnight(0);
        DateTimeOffset finish2 = start2 + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult2 = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start2,
            finish2,
            CancellationToken);

        int secondSeedValue = playout.ProgramScheduleAnchors.Head().EnumeratorState.Seed;

        firstSeedValue.ShouldBe(secondSeedValue);

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task ShuffleFlood_Should_MaintainRandomSeed_MultipleDays()
    {
        var mediaItems = new List<MediaItem>();
        for (var i = 1; i <= 25; i++)
        {
            mediaItems.Add(TestMovie(i, TimeSpan.FromMinutes(55), DateTime.Today.AddHours(i)));
        }

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForItems(mediaItems, PlaybackOrder.Shuffle);
        DateTimeOffset start = HoursAfterMidnight(0).AddSeconds(5);
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
            result.AddedItems.Count.ShouldBe(53);
        }

        // a checkpoint for each of the two midnights the build crossed, plus the continue anchor
        playout.ProgramScheduleAnchors.Count.ShouldBe(3);
        playout.ProgramScheduleAnchors.Count(x => x.AnchorDate is not null).ShouldBe(2);

        PlayoutProgramScheduleAnchor lastCheckpoint = playout.ProgramScheduleAnchors
            .OrderByDescending(a => a.AnchorDate ?? DateTime.MinValue)
            .First();
        lastCheckpoint.EnumeratorState.Seed.ShouldBeGreaterThan(0);
        lastCheckpoint.EnumeratorState.Index.ShouldBe(3);

        // we need to mess up the ordering to trigger the problematic behavior
        // this simulates the way the rows are loaded with EF, with only the checkpoints kept
        var checkpoints = playout.ProgramScheduleAnchors
            .Filter(a => a.AnchorDate is not null)
            .OrderBy(a => a.AnchorDate)
            .ToList();

        PlayoutProgramScheduleAnchor oldest = checkpoints.Head();
        PlayoutProgramScheduleAnchor newest = checkpoints.Last();

        playout.ProgramScheduleAnchors =
        [
            oldest,
            newest
        ];

        int firstSeedValue = lastCheckpoint.EnumeratorState.Seed;

        DateTimeOffset start2 = start.AddHours(1);
        DateTimeOffset finish2 = start2 + TimeSpan.FromDays(2);

        Either<BaseError, PlayoutBuildResult> buildResult2 = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start2,
            finish2,
            CancellationToken);

        PlayoutProgramScheduleAnchor continueAnchor =
            playout.ProgramScheduleAnchors.First(x => x.AnchorDate is null);
        int secondSeedValue = continueAnchor.EnumeratorState.Seed;

        // the continue anchor should have the same seed as the most recent (last) checkpoint from the first run
        firstSeedValue.ShouldBe(secondSeedValue);
    }

    [Test]
    public async Task ShuffleFlood_MultipleSmartCollections_Should_MaintainRandomSeed()
    {
        var mediaItems = new List<MediaItem>
        {
            TestMovie(1, TimeSpan.FromHours(1), DateTime.Today),
            TestMovie(2, TimeSpan.FromHours(1), DateTime.Today.AddHours(1)),
            TestMovie(3, TimeSpan.FromHours(1), DateTime.Today.AddHours(3))
        };

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForSmartCollectionItems(mediaItems, PlaybackOrder.Shuffle);
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
        }

        playout.ProgramScheduleAnchors.Count.ShouldBe(2);
        PlayoutProgramScheduleAnchor primaryAnchor =
            playout.ProgramScheduleAnchors.First(a => a.SmartCollectionId == 1);
        primaryAnchor.EnumeratorState.Seed.ShouldBeGreaterThan(0);
        primaryAnchor.EnumeratorState.Index.ShouldBe(0);

        int firstSeedValue = primaryAnchor.EnumeratorState.Seed;

        DateTimeOffset start2 = HoursAfterMidnight(0);
        DateTimeOffset finish2 = start2 + TimeSpan.FromHours(6);

        Either<BaseError, PlayoutBuildResult> buildResult2 = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start2,
            finish2,
            CancellationToken);

        primaryAnchor = playout.ProgramScheduleAnchors.First(a => a.SmartCollectionId == 1);
        int secondSeedValue = primaryAnchor.EnumeratorState.Seed;

        firstSeedValue.ShouldBe(secondSeedValue);

        primaryAnchor.EnumeratorState.Index.ShouldBe(0);
    }

    [Test]
    public async Task ShuffleFlood_MultipleSmartCollections_Should_MaintainRandomSeed_MultipleDays()
    {
        var mediaItems = new List<MediaItem>();
        for (var i = 1; i <= 100; i++)
        {
            mediaItems.Add(TestMovie(i, TimeSpan.FromMinutes(55), DateTime.Today.AddHours(i)));
        }

        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) =
            TestDataFloodForSmartCollectionItems(mediaItems, PlaybackOrder.Shuffle);
        DateTimeOffset start = HoursAfterMidnight(0).AddSeconds(5);
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
            result.AddedItems.Count.ShouldBe(53);
        }

        // two collections, each with a continue anchor and a checkpoint per midnight crossed
        playout.ProgramScheduleAnchors.Count.ShouldBe(6);
        playout.ProgramScheduleAnchors.Count(x => x.AnchorDate is not null).ShouldBe(4);
        PlayoutProgramScheduleAnchor lastCheckpoint = playout.ProgramScheduleAnchors
            .Filter(psa => psa.SmartCollectionId == 1)
            .OrderByDescending(a => a.AnchorDate ?? DateTime.MinValue)
            .First();
        lastCheckpoint.EnumeratorState.Seed.ShouldBeGreaterThan(0);
        lastCheckpoint.EnumeratorState.Index.ShouldBe(53);

        int firstSeedValue = lastCheckpoint.EnumeratorState.Seed;

        for (var i = 1; i < 20; i++)
        {
            DateTimeOffset start2 = start.AddHours(i);
            DateTimeOffset finish2 = start2 + TimeSpan.FromDays(2);

            Either<BaseError, PlayoutBuildResult> buildResult2 = await builder.Build(
                playout,
                referenceData,
                PlayoutBuildResult.Empty,
                PlayoutBuildMode.Continue,
                start2,
                finish2,
                CancellationToken);

            PlayoutProgramScheduleAnchor continueAnchor =
                playout.ProgramScheduleAnchors
                    .Filter(psa => psa.SmartCollectionId == 1)
                    .First(x => x.AnchorDate is null);
            int secondSeedValue = continueAnchor.EnumeratorState.Seed;

            // the continue anchor should have the same seed as the most recent (last) checkpoint from the first run
            firstSeedValue.ShouldBe(secondSeedValue);
        }
    }

    [Test]
    public async Task FloodContent_Should_FloodWithFixedStartTime_FromAnchor()
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
            Anchor = new PlayoutAnchor
            {
                NextStart = HoursAfterMidnight(9).UtcDateTime,
                ScheduleItemsEnumeratorState = new CollectionEnumeratorState
                {
                    Index = 0,
                    Seed = 1
                },
                InFlood = true
            },
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
        DateTimeOffset finish = start + TimeSpan.FromHours(32);

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(5);

            result.AddedItems[0].StartOffset.ShouldBe(start + TimeSpan.FromHours(9));
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(10));
            result.AddedItems[1].MediaItemId.ShouldBe(2);
            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(11));
            result.AddedItems[2].MediaItemId.ShouldBe(1);

            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(12));
            result.AddedItems[3].MediaItemId.ShouldBe(3);

            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(31));
            result.AddedItems[4].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.InFlood.ShouldBeTrue();

        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task Alternating_MultipleContent_Should_Maintain_Counts()
    {
        var collectionOne = new Collection
        {
            Id = 1,
            Name = "Multiple Items 1",
            MediaItems = [TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1))]
        };

        var collectionTwo = new Collection
        {
            Id = 2,
            Name = "Multiple Items 2",
            MediaItems = [TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 1, 1))]
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
                Count = "3",
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemMultiple
            {
                Id = 2,
                Index = 2,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id,
                StartTime = null,
                Count = "3",
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
            Anchor = new PlayoutAnchor
            {
                NextStart = HoursAfterMidnight(1).UtcDateTime,
                ScheduleItemsEnumeratorState = new CollectionEnumeratorState
                {
                    Index = 0,
                    Seed = 1
                },
                MultipleRemaining = 2
            },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(playout.Channel, Option<Deco>.None, [], [], playout.ProgramSchedule, [], [],
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
            PlayoutBuildMode.Continue,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(4);

            result.AddedItems[0].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[1].MediaItemId.ShouldBe(1);

            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[2].MediaItemId.ShouldBe(2);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(4));
            result.AddedItems[3].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.ScheduleItemsEnumeratorState.Index.ShouldBe(1);
        playout.Anchor.MultipleRemaining.ShouldBe(1);
        playout.Anchor.NextStartOffset.ShouldBe(finish);
    }

    [Test]
    public async Task Alternating_Duration_Should_Complete_Duration()
    {
        var collectionOne = new Collection
        {
            Id = 1,
            Name = "Duration Items 1",
            MediaItems = [TestMovie(1, TimeSpan.FromHours(1), new DateTime(2020, 1, 1))]
        };

        var collectionTwo = new Collection
        {
            Id = 2,
            Name = "Duration Items 2",
            MediaItems = [TestMovie(2, TimeSpan.FromHours(1), new DateTime(2020, 1, 1))]
        };

        var fakeRepository = new FakeMediaCollectionRepository(
            Map(
                (collectionOne.Id, collectionOne.MediaItems.ToList()),
                (collectionTwo.Id, collectionTwo.MediaItems.ToList())));

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
                TailMode = TailMode.None,
                PlaybackOrder = PlaybackOrder.Chronological
            },
            new ProgramScheduleItemDuration
            {
                Id = 2,
                Index = 2,
                Collection = collectionTwo,
                CollectionId = collectionTwo.Id,
                StartTime = null,
                PlayoutDuration = TimeSpan.FromHours(3),
                TailMode = TailMode.None,
                PlaybackOrder = PlaybackOrder.Chronological
            }
        };

        DateTimeOffset start = HoursAfterMidnight(0);
        DateTimeOffset finish = start + TimeSpan.FromHours(5);

        var playout = new Playout
        {
            ProgramSchedule = new ProgramSchedule
            {
                Items = items
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            Anchor = new PlayoutAnchor
            {
                NextStart = (start + TimeSpan.FromHours(1)).UtcDateTime,
                ScheduleItemsEnumeratorState = new CollectionEnumeratorState
                {
                    Index = 0,
                    Seed = 1
                },
                DurationFinish = (start + TimeSpan.FromHours(3)).UtcDateTime
            },
            ProgramScheduleAnchors = [],
            Items = [],
            ProgramScheduleAlternates = [],
            FillGroupIndices = []
        };

        var referenceData =
            new PlayoutReferenceData(playout.Channel, Option<Deco>.None, [], [], playout.ProgramSchedule, [], [],
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

        Either<BaseError, PlayoutBuildResult> buildResult = await builder.Build(
            playout,
            referenceData,
            PlayoutBuildResult.Empty,
            PlayoutBuildMode.Continue,
            start,
            finish,
            CancellationToken);

        buildResult.IsRight.ShouldBeTrue();
        foreach (var result in buildResult.RightToSeq())
        {
            result.AddedItems.Count.ShouldBe(5);

            result.AddedItems[0].StartOffset.ShouldBe(start + TimeSpan.FromHours(1));
            result.AddedItems[0].MediaItemId.ShouldBe(1);
            result.AddedItems[1].StartOffset.ShouldBe(start + TimeSpan.FromHours(2));
            result.AddedItems[1].MediaItemId.ShouldBe(1);

            result.AddedItems[2].StartOffset.ShouldBe(start + TimeSpan.FromHours(3));
            result.AddedItems[2].MediaItemId.ShouldBe(2);
            result.AddedItems[3].StartOffset.ShouldBe(start + TimeSpan.FromHours(4));
            result.AddedItems[3].MediaItemId.ShouldBe(2);
            result.AddedItems[4].StartOffset.ShouldBe(start + TimeSpan.FromHours(5));
            result.AddedItems[4].MediaItemId.ShouldBe(2);
        }

        playout.Anchor.ScheduleItemsEnumeratorState.Index.ShouldBe(0);
        playout.Anchor.DurationFinish.ShouldBeNull();
        playout.Anchor.NextStartOffset.ShouldBe(start + TimeSpan.FromHours(6));
    }
}

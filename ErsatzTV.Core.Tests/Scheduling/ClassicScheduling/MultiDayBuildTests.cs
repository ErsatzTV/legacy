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

// unit-test version of the "fast forward a playout" debugging endpoint that used to live in
// ChannelController
[TestFixture]
public class MultiDayBuildTests : PlayoutBuilderTestBase
{
    private const int DaysToBuild = 2;
    private const int WeekOfDaysToBuild = 7;

    [Test]
    public async Task Continue_Should_Keep_A_Checkpoint_For_The_Current_Day()
    {
        // 6 hour movies 3 at a time, so an 18 hour block regularly crosses midnight
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = MultipleTestData(
            TimeSpan.FromHours(6),
            multipleCount: "3");

        DateTimeOffset start = LocalTime(20);
        var missing = new List<string>();

        for (var hour = 0; hour < 24 * 7; hour++)
        {
            DateTimeOffset now = start.AddHours(hour);

            Either<BaseError, PlayoutBuildResult> result = await builder.Build(
                now,
                playout,
                referenceData,
                PlayoutBuildMode.Continue,
                CancellationToken);

            result.IsRight.ShouldBeTrue($"build failed at {now:yyyy-MM-dd HH:mm}");

            // no checkpoint is written for a build's own first day, and none is needed: there is no
            // earlier state, so refreshing from index 0 reproduces the same schedule
            if (now.Date == start.Date)
            {
                continue;
            }

            bool hasCheckpointForToday = playout.ProgramScheduleAnchors.Any(a =>
                a.AnchorDateOffset.HasValue && a.AnchorDateOffset.Value.Date == now.Date);

            if (!hasCheckpointForToday)
            {
                missing.Add($"{now:yyyy-MM-dd HH:mm}");
            }
        }

        missing.ShouldBeEmpty(
            $"[tz {TimeZoneInfo.Local.Id}] no checkpoint for the current day at {missing.Count} hour(s), "
            + $"first: {missing.FirstOrDefault()}");
    }

    [Test]
    public async Task Refresh_Should_Not_Reset_Collection_Progress()
    {
        // large enough that the index can't wrap, so it measures progress directly
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = MultipleTestData(
            TimeSpan.FromHours(6),
            multipleCount: "3",
            itemCount: 300);

        DateTimeOffset start = LocalTime(20);

        // long enough that progress is clearly distinguishable from a playout that restarted
        for (var hour = 0; hour < 24 * 21; hour++)
        {
            await builder.Build(
                start.AddHours(hour),
                playout,
                referenceData,
                PlayoutBuildMode.Continue,
                CancellationToken);
        }

        int before = HeadIndex(playout);
        before.ShouldBeGreaterThan(0, "the fast forward should have consumed some of the collection");

        Either<BaseError, PlayoutBuildResult> result = await builder.Build(
            start.AddDays(21),
            playout,
            referenceData,
            PlayoutBuildMode.Refresh,
            CancellationToken);

        result.IsRight.ShouldBeTrue();

        // rewinding to the start of today legitimately moves the index back by up to a day; a refresh
        // that lost its checkpoints lands an order of magnitude lower
        int after = HeadIndex(playout);
        after.ShouldBeGreaterThan(
            before / 2,
            $"[tz {TimeZoneInfo.Local.Id}] refresh reset collection progress (was {before}, now {after})");
    }

    [Test]
    public async Task Continue_Should_Keep_A_Checkpoint_For_Every_Day_In_The_Build_Window()
    {
        // shaped like the playouts that were missing checkpoints in a real user database
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = MultipleTestData(
            TimeSpan.FromMinutes(100),
            multipleCount: "5",
            itemCount: 300,
            daysToBuild: WeekOfDaysToBuild,
            shuffleScheduleItems: true,
            scheduleItemCount: 3);

        DateTimeOffset start = LocalTime(20);
        var missing = new List<string>();

        for (var hour = 0; hour < 24 * 21; hour++)
        {
            DateTimeOffset now = start.AddHours(hour);

            Either<BaseError, PlayoutBuildResult> result = await builder.Build(
                now,
                playout,
                referenceData,
                PlayoutBuildMode.Continue,
                CancellationToken);

            result.IsRight.ShouldBeTrue($"build failed at {now:yyyy-MM-dd HH:mm}");

            // every day covered needs a checkpoint, except the build's own first day; past days are pruned
            DateTime firstExpected = now.Date > start.Date ? now.Date : start.Date.AddDays(1);
            DateTime lastExpected = now.AddDays(WeekOfDaysToBuild).Date;

            for (DateTime date = firstExpected; date <= lastExpected; date = date.AddDays(1))
            {
                bool hasCheckpoint = playout.ProgramScheduleAnchors.Any(a =>
                    a.AnchorDateOffset.HasValue && a.AnchorDateOffset.Value.Date == date);

                if (!hasCheckpoint)
                {
                    missing.Add($"{date:yyyy-MM-dd} (at {now:MM-dd HH:mm})");
                }
            }
        }

        missing.ShouldBeEmpty(
            $"[tz {TimeZoneInfo.Local.Id}] {missing.Count} day(s) in the build window had no checkpoint, "
            + $"first: {missing.FirstOrDefault()}");
    }

    private static int HeadIndex(Playout playout) =>
        playout.ProgramScheduleAnchors
            .Filter(a => a.AnchorDate is null)
            .Map(a => a.EnumeratorState.Index)
            .HeadOrNone()
            .IfNone(-1);

    private (PlayoutBuilder, Playout, PlayoutReferenceData) MultipleTestData(
        TimeSpan itemDuration,
        string multipleCount,
        int itemCount = 5,
        int daysToBuild = DaysToBuild,
        bool shuffleScheduleItems = false,
        int scheduleItemCount = 1)
    {
        var collection = new Collection
        {
            Id = 1,
            Name = "Test Collection",
            MediaItems = Enumerable.Range(1, itemCount)
                .Map(i => (MediaItem)TestMovie(i, itemDuration, new DateTime(2000 + i, 1, 1)))
                .ToList()
        };

        var collectionRepo = new FakeMediaCollectionRepository(Map((collection.Id, collection.MediaItems.ToList())));

        IConfigElementRepository configRepo = Substitute.For<IConfigElementRepository>();

        // ConfigElementKey has no value equality and hands out a new instance on every access, so
        // the key has to be matched on its string, not with Arg.Is(theKey)
        configRepo
            .GetValue<int>(
                Arg.Is<ConfigElementKey>(k => k.Key == ConfigElementKey.PlayoutDaysToBuild.Key),
                Arg.Any<CancellationToken>())
            .Returns(Some(daysToBuild));

        var televisionRepo = new FakeTelevisionRepository();
        IArtistRepository artistRepo = Substitute.For<IArtistRepository>();
        IMultiEpisodeShuffleCollectionEnumeratorFactory factory =
            Substitute.For<IMultiEpisodeShuffleCollectionEnumeratorFactory>();
        IRerunHelper rerunHelper = Substitute.For<IRerunHelper>();

        var builder = new PlayoutBuilder(
            configRepo,
            collectionRepo,
            televisionRepo,
            artistRepo,
            factory,
            new MockFileSystem(),
            rerunHelper,
            Logger);

        var items = Enumerable.Range(1, scheduleItemCount)
            .Map(i => (ProgramScheduleItem)new ProgramScheduleItemMultiple
            {
                Id = i,
                Index = i,
                CollectionType = CollectionType.Collection,
                Collection = collection,
                CollectionId = collection.Id,
                StartTime = null,
                PlaybackOrder = PlaybackOrder.Chronological,
                Count = multipleCount
            })
            .ToList();

        var playout = new Playout
        {
            Id = 1,
            ScheduleKind = PlayoutScheduleKind.Classic,
            ProgramSchedule = new ProgramSchedule
            {
                Items = items,
                ShuffleScheduleItems = shuffleScheduleItems
            },
            Channel = new Channel(Guid.Empty) { Id = 1, Name = "Test Channel" },
            Items = [],
            ProgramScheduleAnchors = [],
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

        return (builder, playout, referenceData);
    }

    // anchor dates are compared in machine-local time, so the test clock has to be local too or day
    // boundaries won't line up. that means running in whatever zone the machine is in, hence mid-June,
    // where no zone has a DST transition
    private static DateTimeOffset LocalTime(int hour)
    {
        var date = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)) + TimeSpan.FromHours(hour);
    }
}

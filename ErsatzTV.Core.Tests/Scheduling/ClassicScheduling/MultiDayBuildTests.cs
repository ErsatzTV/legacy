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

/// <summary>
///     Unit-test version of the "fast forward a playout" debugging endpoint that used to live in
///     ChannelController: run hourly continue builds across several days and assert invariants at
///     every step.
/// </summary>
[TestFixture]
public class MultiDayBuildTests : PlayoutBuilderTestBase
{
    private const int DaysToBuild = 2;
    private const int WeekOfDaysToBuild = 7;

    [Test]
    public async Task Continue_Should_Keep_A_Checkpoint_For_The_Current_Day()
    {
        // 6 hour movies scheduled 3 at a time, so a block spans 18 hours and regularly
        // crosses midnight - which is expected and intended
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = MultipleTestData(
            TimeSpan.FromHours(6),
            multipleCount: "3");

        // a playout created at 20:00, then built every hour for a week
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

            // a build never writes a checkpoint for its own first day, and doesn't need to:
            // there is no earlier state to restore, so refreshing starts from index 0 and
            // reproduces the same schedule
            if (now.Date == start.Date)
            {
                continue;
            }

            // from here on, a refresh has to have somewhere to rewind to
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
        // a collection large enough that the index can't wrap over the whole run, so the
        // index is a straightforward measure of how much progress has been made
        (PlayoutBuilder builder, Playout playout, PlayoutReferenceData referenceData) = MultipleTestData(
            TimeSpan.FromHours(6),
            multipleCount: "3",
            itemCount: 300);

        DateTimeOffset start = LocalTime(20);

        // three weeks of hourly builds, so that progress is far enough along to be clearly
        // distinguishable from a playout that restarted at the beginning
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

        // a refresh rewinds to the start of today, so the index legitimately moves back by
        // up to a day. a refresh that lost its checkpoints restarts at 0 and only advances
        // through the build window, landing an order of magnitude lower
        int after = HeadIndex(playout);
        after.ShouldBeGreaterThan(
            before / 2,
            $"[tz {TimeZoneInfo.Local.Id}] refresh reset collection progress (was {before}, now {after})");
    }

    [Test]
    public async Task Continue_Should_Keep_A_Checkpoint_For_Every_Day_In_The_Build_Window()
    {
        // shaped like the playouts that were missing checkpoints in a real user database: a week
        // long build window, shuffled schedule items, and blocks of five ~100 minute items
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

            // every day the playout covers needs a checkpoint, so that a refresh on any of them has
            // somewhere to rewind to. the build's own first day is the one exception, and days
            // already in the past are pruned
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

    /// <summary>
    ///     Anchor dates are compared in machine-local time
    ///     (<see cref="PlayoutProgramScheduleAnchor.AnchorDateOffset" />), so the test clock has to be local
    ///     too, or day boundaries won't line up. That means these tests run in whatever zone the machine is
    ///     in - UTC on CI, something else locally - so the window below is deliberately placed in mid-June,
    ///     where no zone has a DST transition. Day-boundary behaviour across a DST change is a separate
    ///     concern and wants its own test.
    /// </summary>
    private static DateTimeOffset LocalTime(int hour)
    {
        var date = new DateTime(2025, 6, 10, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)) + TimeSpan.FromHours(hour);
    }
}

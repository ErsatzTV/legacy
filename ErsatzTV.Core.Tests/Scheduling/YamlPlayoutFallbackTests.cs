using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class YamlPlayoutFallbackTests
{
    private static readonly DateTimeOffset Start = new(2025, 4, 15, 12, 0, 0, TimeSpan.FromHours(-5));

    [Test]
    public async Task Fallback_Should_Not_Keep_Out_Point_Of_Content_That_Did_Not_Fit()
    {
        // the gap is ten minutes; the next content item runs thirty, so it is
        // rejected and the fallback takes its place
        var content = TestMovie(1, TimeSpan.FromMinutes(30));
        var fallback = TestMovie(2, TimeSpan.FromMinutes(60));

        var context = new YamlPlayoutContext(new Playout { Id = 1 }, new YamlPlayoutDefinition(), 1)
        {
            CurrentTime = Start
        };

        await TestableDurationHandler.Run(
            context,
            targetTime: Start + TimeSpan.FromMinutes(10),
            new TestEnumerator(content),
            new TestEnumerator(fallback));

        context.AddedItems.Count.ShouldBe(1);

        PlayoutItem item = context.AddedItems[0];
        item.FillerKind.ShouldBe(FillerKind.Fallback);
        item.MediaItemId.ShouldBe(fallback.Id);

        // the out point belongs to the fallback filling a ten minute gap, not
        // to the thirty minute item it replaced
        item.OutPoint.ShouldBe(TimeSpan.FromMinutes(10));
        (item.Finish - item.Start).ShouldBe(item.OutPoint - item.InPoint);
    }

    [Test]
    public async Task Fallback_Out_Point_Should_Match_Gap_When_Shorter_Than_Fallback()
    {
        var content = TestMovie(1, TimeSpan.FromMinutes(30));
        var fallback = TestMovie(2, TimeSpan.FromMinutes(45));

        var context = new YamlPlayoutContext(new Playout { Id = 1 }, new YamlPlayoutDefinition(), 1)
        {
            CurrentTime = Start
        };

        await TestableDurationHandler.Run(
            context,
            targetTime: Start + TimeSpan.FromSeconds(90),
            new TestEnumerator(content),
            new TestEnumerator(fallback));

        context.AddedItems.Count.ShouldBe(1);
        context.AddedItems[0].OutPoint.ShouldBe(TimeSpan.FromSeconds(90));
    }

    private static Movie TestMovie(int id, TimeSpan duration) =>
        new()
        {
            Id = id,
            MovieMetadata = [new MovieMetadata { ReleaseDate = new DateTime(2000, 1, 1) }],
            MediaVersions = [new MediaVersion { Duration = duration, Chapters = [] }]
        };

    /// <summary>
    /// Reaches the protected scheduling routine with hand-built enumerators, so
    /// the fallback path can be exercised without a content database.
    /// </summary>
    private class TestableDurationHandler : YamlPlayoutDurationHandler
    {
        private TestableDurationHandler() : base(null)
        {
        }

        public static Task<DateTimeOffset> Run(
            YamlPlayoutContext context,
            DateTimeOffset targetTime,
            IMediaCollectionEnumerator enumerator,
            IMediaCollectionEnumerator fallbackEnumerator) =>
            Schedule(
                context,
                contentKey: "content",
                fallbackContentKey: "fallback",
                targetTime,
                stopBeforeEnd: true,
                discardAttempts: 0,
                trim: false,
                offlineTail: false,
                FillerKind.None,
                customTitle: null,
                disableWatermarks: false,
                enumerator,
                Option<IMediaCollectionEnumerator>.Some(fallbackEnumerator),
                _ => Task.CompletedTask,
                NullLogger<SequentialPlayoutBuilder>.Instance);
    }

    private class TestEnumerator(MediaItem mediaItem) : IMediaCollectionEnumerator
    {
        public string SchedulingContextName => "test";
        public CollectionEnumeratorState State { get; } = new() { Index = 0, Seed = 0 };
        public Option<MediaItem> Current => mediaItem;
        public Option<bool> CurrentIncludeInProgramGuide => Option<bool>.None;
        public int Count => 1;
        public Option<TimeSpan> MinimumDuration => mediaItem.GetDurationForPlayout();
        public void ResetState(CollectionEnumeratorState state) { }
        public void MoveNext(Option<DateTimeOffset> scheduledAt) { }
    }
}

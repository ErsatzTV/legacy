using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.Engine;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling.Engine;

[TestFixture]
public class SchedulingEngineTests
{
    [Test]
    public void Continue_Across_Time_Change()
    {
        var engine = new SchedulingEngine(
            Substitute.For<IMediaCollectionRepository>(),
            Substitute.For<IGraphicsElementRepository>(),
            Substitute.For<IChannelRepository>(),
            Substitute.For<ILogger<SchedulingEngine>>());

        var anchor = new PlayoutAnchor
        {
            NextStart = new DateTimeOffset(new DateTime(2025, 10, 26), TimeSpan.FromHours(-5)).UtcDateTime
        };

        var start = new DateTimeOffset(new DateTime(2025, 11, 20), TimeSpan.FromHours(-6));
        var finish = start.AddDays(1);

        engine.BuildBetween(start, finish);

        // should not throw
        engine.RestoreOrReset(anchor);
    }

    [Test]
    public async Task Fallback_Should_Not_Keep_Out_Point_Of_Content_That_Did_Not_Fit()
    {
        var content = TestMovie(1, TimeSpan.FromMinutes(30));
        var fallback = TestMovie(2, TimeSpan.FromMinutes(60));

        var mediaCollectionRepository = Substitute.For<IMediaCollectionRepository>();
        mediaCollectionRepository.GetCollectionItemsByName("content", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<MediaItem>>([content]));
        mediaCollectionRepository.GetCollectionItemsByName("fallback", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<List<MediaItem>>([fallback]));

        var engine = new SchedulingEngine(
            mediaCollectionRepository,
            Substitute.For<IGraphicsElementRepository>(),
            Substitute.For<IChannelRepository>(),
            Substitute.For<ILogger<SchedulingEngine>>());

        var start = new DateTimeOffset(2025, 4, 15, 12, 0, 0, TimeSpan.FromHours(-5));

        engine.WithPlayoutId(1)
            .WithReferenceData(ReferenceData())
            .BuildBetween(start, start.AddDays(1));

        await engine.AddCollection("content", "content", PlaybackOrder.Chronological, CancellationToken.None);
        await engine.AddCollection("fallback", "fallback", PlaybackOrder.Chronological, CancellationToken.None);

        // the gap is ten minutes; the content item runs thirty, so it does not
        // fit and the fallback takes its place
        engine.AddDuration(
            "content",
            "00:10:00",
            "fallback",
            trim: false,
            discardAttempts: 0,
            stopBeforeEnd: true,
            offlineTail: false,
            Option<FillerKind>.None,
            customTitle: null,
            disableWatermarks: false);

        List<PlayoutItem> addedItems = engine.GetState().AddedItems;
        addedItems.Count.ShouldBe(1);

        PlayoutItem item = addedItems[0];
        item.FillerKind.ShouldBe(FillerKind.Fallback);
        item.MediaItemId.ShouldBe(fallback.Id);

        // the out point belongs to the fallback filling a ten minute gap, not
        // to the thirty minute item it replaced
        item.OutPoint.ShouldBe(TimeSpan.FromMinutes(10));
        (item.Finish - item.Start).ShouldBe(item.OutPoint - item.InPoint);
    }

    private static PlayoutReferenceData ReferenceData() =>
        new(
            new Channel(Guid.Empty) { Id = 1 },
            Option<Deco>.None,
            [],
            [],
            new ProgramSchedule(),
            [],
            [],
            TimeSpan.FromDays(1));

    private static Movie TestMovie(int id, TimeSpan duration) =>
        new()
        {
            Id = id,
            MovieMetadata = [new MovieMetadata { ReleaseDate = new DateTime(2000, 1, 1) }],
            MediaVersions = [new MediaVersion { Duration = duration, Chapters = [] }]
        };
}

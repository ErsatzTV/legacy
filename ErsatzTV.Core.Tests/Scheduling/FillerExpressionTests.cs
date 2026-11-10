using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class FillerExpressionTests
{
    [Test]
    public void Two_Points_In_30_Minute_Content()
    {
        // 30 min content
        var playoutItem = new PlayoutItem { Start = DateTimeOffset.Now.UtcDateTime };
        playoutItem.Finish = playoutItem.Start + TimeSpan.FromMinutes(30);

        // chapters every 5 min
        var chapters = new List<MediaChapter>
        {
            new() { ChapterId = 1, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromMinutes(5) },
            new() { ChapterId = 2, StartTime = TimeSpan.FromMinutes(5), EndTime = TimeSpan.FromMinutes(10) },
            new() { ChapterId = 3, StartTime = TimeSpan.FromMinutes(10), EndTime = TimeSpan.FromMinutes(15) },
            new() { ChapterId = 4, StartTime = TimeSpan.FromMinutes(15), EndTime = TimeSpan.FromMinutes(20) },
            new() { ChapterId = 5, StartTime = TimeSpan.FromMinutes(20), EndTime = TimeSpan.FromMinutes(25) },
            new() { ChapterId = 6, StartTime = TimeSpan.FromMinutes(25), EndTime = TimeSpan.FromMinutes(30) }
        };

        // skip first 5 min of content, wait at least 5 min between points, only match up to 2 points
        var fillerPreset = new FillerPreset
        {
            FillerKind = FillerKind.MidRoll,
            Expression = "(point > 5 * 60) and (last_mid_filler > 5 * 60) and (matched_points < 2)"
        };

        List<MediaChapter> result = FillerExpression.FilterChapters(fillerPreset.Expression, chapters, playoutItem, "Test", NullLogger.Instance);

        result.Count.ShouldBe(3);
        result[0].EndTime.ShouldBe(TimeSpan.FromMinutes(10));
        result[1].EndTime.ShouldBe(TimeSpan.FromMinutes(20));
        result[2].EndTime.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Test]
    public void Two_Points_In_30_Minute_Content_Another_Expression()
    {
        // 30 min content
        var playoutItem = new PlayoutItem { Start = DateTimeOffset.Now.UtcDateTime };
        playoutItem.Finish = playoutItem.Start + TimeSpan.FromMinutes(30);

        // chapters every 5 min
        var chapters = new List<MediaChapter>
        {
            new() { ChapterId = 1, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromMinutes(5) },
            new() { ChapterId = 2, StartTime = TimeSpan.FromMinutes(5), EndTime = TimeSpan.FromMinutes(10) },
            new() { ChapterId = 3, StartTime = TimeSpan.FromMinutes(10), EndTime = TimeSpan.FromMinutes(15) },
            new() { ChapterId = 4, StartTime = TimeSpan.FromMinutes(15), EndTime = TimeSpan.FromMinutes(20) },
            new() { ChapterId = 5, StartTime = TimeSpan.FromMinutes(20), EndTime = TimeSpan.FromMinutes(25) },
            new() { ChapterId = 6, StartTime = TimeSpan.FromMinutes(25), EndTime = TimeSpan.FromMinutes(30) }
        };

        // skip first 5 min of content, wait at least 5 min between points, only match up to 2 points
        var fillerPreset = new FillerPreset
        {
            FillerKind = FillerKind.MidRoll,
            Expression =
                "(total_progress >= 0.2 and matched_points = 0) or (total_progress >= 0.6 and matched_points = 1)"
        };

        List<MediaChapter> result = FillerExpression.FilterChapters(fillerPreset.Expression, chapters, playoutItem, "Test", NullLogger.Instance);

        result.Count.ShouldBe(3);
        result[0].EndTime.ShouldBe(TimeSpan.FromMinutes(10));
        result[1].EndTime.ShouldBe(TimeSpan.FromMinutes(20));
        result[2].EndTime.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Test]
    public void Match_Case_Insensitive_Titles_Expression()
    {
        // 30 min content
        var playoutItem = new PlayoutItem { Start = DateTimeOffset.Now.UtcDateTime };
        playoutItem.Finish = playoutItem.Start + TimeSpan.FromMinutes(30);

        // chapters every 5 min
        var chapters = new List<MediaChapter>
        {
            new() { ChapterId = 1, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromMinutes(5), Title = "Not Here" },
            new() { ChapterId = 2, StartTime = TimeSpan.FromMinutes(5), EndTime = TimeSpan.FromMinutes(10), Title = "Here" },
            new() { ChapterId = 3, StartTime = TimeSpan.FromMinutes(10), EndTime = TimeSpan.FromMinutes(15), Title = "Not Here" },
            new() { ChapterId = 4, StartTime = TimeSpan.FromMinutes(15), EndTime = TimeSpan.FromMinutes(20), Title = "Here" },
            new() { ChapterId = 5, StartTime = TimeSpan.FromMinutes(20), EndTime = TimeSpan.FromMinutes(25), Title = "Not Here" },
            new() { ChapterId = 6, StartTime = TimeSpan.FromMinutes(25), EndTime = TimeSpan.FromMinutes(30), Title = "Here" }
        };

        var fillerPreset = new FillerPreset
        {
            FillerKind = FillerKind.MidRoll,
            Expression =
                "title == 'here'"
        };

        List<MediaChapter> result = FillerExpression.FilterChapters(fillerPreset.Expression, chapters, playoutItem, "Test", NullLogger.Instance);

        result.Count.ShouldBe(3);
        result[0].EndTime.ShouldBe(TimeSpan.FromMinutes(10));
        result[1].EndTime.ShouldBe(TimeSpan.FromMinutes(20));
        result[2].EndTime.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Test]
    public void Exclude_Case_Insensitive_Titles_Expression()
    {
        // 30 min content
        var playoutItem = new PlayoutItem { Start = DateTimeOffset.Now.UtcDateTime };
        playoutItem.Finish = playoutItem.Start + TimeSpan.FromMinutes(30);

        // chapters every 5 min
        var chapters = new List<MediaChapter>
        {
            new() { ChapterId = 1, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromMinutes(5), Title = "Not Here" },
            new() { ChapterId = 2, StartTime = TimeSpan.FromMinutes(5), EndTime = TimeSpan.FromMinutes(10), Title = "Here" },
            new() { ChapterId = 3, StartTime = TimeSpan.FromMinutes(10), EndTime = TimeSpan.FromMinutes(15), Title = "Not Here" },
            new() { ChapterId = 4, StartTime = TimeSpan.FromMinutes(15), EndTime = TimeSpan.FromMinutes(20), Title = "Here" },
            new() { ChapterId = 5, StartTime = TimeSpan.FromMinutes(20), EndTime = TimeSpan.FromMinutes(25), Title = "Not Here" },
            new() { ChapterId = 6, StartTime = TimeSpan.FromMinutes(25), EndTime = TimeSpan.FromMinutes(30), Title = "Here" }
        };

        var fillerPreset = new FillerPreset
        {
            FillerKind = FillerKind.MidRoll,
            Expression =
                "title != 'not here'"
        };

        List<MediaChapter> result = FillerExpression.FilterChapters(fillerPreset.Expression, chapters, playoutItem, "Test", NullLogger.Instance);

        result.Count.ShouldBe(3);
        result[0].EndTime.ShouldBe(TimeSpan.FromMinutes(10));
        result[1].EndTime.ShouldBe(TimeSpan.FromMinutes(20));
        result[2].EndTime.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Test]
    public void Include_Partial_Case_Insensitive_Titles_Expression()
    {
        // 30 min content
        var playoutItem = new PlayoutItem { Start = DateTimeOffset.Now.UtcDateTime };
        playoutItem.Finish = playoutItem.Start + TimeSpan.FromMinutes(30);

        // chapters every 5 min
        var chapters = new List<MediaChapter>
        {
            new() { ChapterId = 1, StartTime = TimeSpan.Zero, EndTime = TimeSpan.FromMinutes(5), Title = "Not Here" },
            new() { ChapterId = 2, StartTime = TimeSpan.FromMinutes(5), EndTime = TimeSpan.FromMinutes(10), Title = "Here" },
            new() { ChapterId = 3, StartTime = TimeSpan.FromMinutes(10), EndTime = TimeSpan.FromMinutes(15), Title = "Not Here" },
            new() { ChapterId = 4, StartTime = TimeSpan.FromMinutes(15), EndTime = TimeSpan.FromMinutes(20), Title = "Here" },
            new() { ChapterId = 5, StartTime = TimeSpan.FromMinutes(20), EndTime = TimeSpan.FromMinutes(25), Title = "Not Here" },
            new() { ChapterId = 6, StartTime = TimeSpan.FromMinutes(25), EndTime = TimeSpan.FromMinutes(30), Title = "Here" }
        };

        var fillerPreset = new FillerPreset
        {
            FillerKind = FillerKind.MidRoll,
            Expression =
                "title like \"%here%\""
        };

        List<MediaChapter> result = FillerExpression.FilterChapters(fillerPreset.Expression, chapters, playoutItem, "Test", NullLogger.Instance);

        result.Count.ShouldBe(6);
        result[0].EndTime.ShouldBe(TimeSpan.FromMinutes(5));
        result[1].EndTime.ShouldBe(TimeSpan.FromMinutes(10));
        result[2].EndTime.ShouldBe(TimeSpan.FromMinutes(15));
        result[3].EndTime.ShouldBe(TimeSpan.FromMinutes(20));
        result[4].EndTime.ShouldBe(TimeSpan.FromMinutes(25));
        result[5].EndTime.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Test]
    public void Should_Not_Change_The_Input_Chapters()
    {
        var playoutItem = new PlayoutItem { Start = DateTimeOffset.Now.UtcDateTime };
        playoutItem.Finish = playoutItem.Start + TimeSpan.FromMinutes(30);

        List<MediaChapter> chapters = SixChapters();

        List<MediaChapter> first = FillerExpression.FilterChapters(
            "num = 2 or num = 4",
            chapters,
            playoutItem,
            "Test",
            NullLogger.Instance);

        // a second use of the same chapters, as a later playout item or the pad filler does
        List<MediaChapter> second = FillerExpression.FilterChapters(
            "num = 2 or num = 4",
            chapters,
            playoutItem,
            "Test",
            NullLogger.Instance);

        for (var i = 0; i < chapters.Count; i++)
        {
            chapters[i].StartTime.ShouldBe(TimeSpan.FromMinutes(i * 5));
        }

        second.Map(c => (c.StartTime, c.EndTime)).ShouldBe(first.Map(c => (c.StartTime, c.EndTime)));
        second[1].StartTime.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [TestCase("num >")]
    [TestCase("chapter_num > 2")]
    public void Invalid_Expression_Should_Use_No_Points(string expression)
    {
        var playoutItem = new PlayoutItem { Start = DateTimeOffset.Now.UtcDateTime };
        playoutItem.Finish = playoutItem.Start + TimeSpan.FromMinutes(30);

        List<MediaChapter> chapters = SixChapters();

        List<MediaChapter> result = FillerExpression.FilterChapters(
            expression,
            chapters,
            playoutItem,
            "Test",
            NullLogger.Instance);

        result.Count.ShouldBe(1);
        result[0].StartTime.ShouldBe(TimeSpan.Zero);
        result[0].EndTime.ShouldBe(TimeSpan.FromMinutes(30));
    }

    private static List<MediaChapter> SixChapters() =>
        Range(0, 6).Map(i => new MediaChapter
            {
                ChapterId = i + 1,
                StartTime = TimeSpan.FromMinutes(i * 5),
                EndTime = TimeSpan.FromMinutes((i + 1) * 5)
            })
            .ToList();
}

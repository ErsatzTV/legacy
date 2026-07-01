using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

public static class YamlPlayoutScheduleSelectionTests
{
    [TestFixture]
    public class GetScheduleForDate
    {
        private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

        [Test]
        public void Should_Return_None_When_No_Schedules()
        {
            AlternateScheduleSelector
                .GetScheduleForDate(
                    new List<YamlPlayoutScheduleItem>(),
                    new DateTimeOffset(2025, 4, 15, 12, 0, 0, Offset))
                .IsNone.ShouldBeTrue();
        }

        [Test]
        public void Should_Match_Annual_Range_Every_Year()
        {
            var april = new YamlPlayoutScheduleItem
            {
                Name = "April",
                StartDate = "04-01",
                EndDate = "04-30"
            };

            AlternateScheduleSelector
                .GetScheduleForDate([april], new DateTimeOffset(2025, 4, 15, 12, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(april);

            AlternateScheduleSelector
                .GetScheduleForDate([april], new DateTimeOffset(2030, 4, 1, 0, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(april);
        }

        [Test]
        public void Should_Not_Match_Annual_Range_Outside_Dates()
        {
            var april = new YamlPlayoutScheduleItem
            {
                Name = "April",
                StartDate = "04-01",
                EndDate = "04-30"
            };

            AlternateScheduleSelector
                .GetScheduleForDate([april], new DateTimeOffset(2025, 5, 1, 0, 0, 0, Offset))
                .IsNone.ShouldBeTrue();
        }

        [Test]
        public void Should_Match_Specific_Year_Only()
        {
            var christmas = new YamlPlayoutScheduleItem
            {
                Name = "Christmas 2025",
                StartDate = "2025-12-25",
                EndDate = "2025-12-25"
            };

            AlternateScheduleSelector
                .GetScheduleForDate([christmas], new DateTimeOffset(2025, 12, 25, 8, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(christmas);

            AlternateScheduleSelector
                .GetScheduleForDate([christmas], new DateTimeOffset(2026, 12, 25, 8, 0, 0, Offset))
                .IsNone.ShouldBeTrue();
        }

        [Test]
        public void Should_Match_Annual_Range_That_Wraps_Year_Boundary()
        {
            var winter = new YamlPlayoutScheduleItem
            {
                Name = "Winter",
                StartDate = "12-01",
                EndDate = "01-15"
            };

            AlternateScheduleSelector
                .GetScheduleForDate([winter], new DateTimeOffset(2025, 12, 20, 0, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(winter);

            AlternateScheduleSelector
                .GetScheduleForDate([winter], new DateTimeOffset(2026, 1, 10, 0, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(winter);

            AlternateScheduleSelector
                .GetScheduleForDate([winter], new DateTimeOffset(2026, 6, 1, 0, 0, 0, Offset))
                .IsNone.ShouldBeTrue();
        }

        [Test]
        public void Should_Prefer_Higher_Priority_On_Overlap()
        {
            var december = new YamlPlayoutScheduleItem
            {
                Name = "December",
                StartDate = "12-01",
                EndDate = "12-31",
                Priority = 0
            };

            var christmasDay = new YamlPlayoutScheduleItem
            {
                Name = "Christmas Day",
                StartDate = "12-25",
                EndDate = "12-25",
                Priority = 10
            };

            AlternateScheduleSelector
                .GetScheduleForDate([december, christmasDay], new DateTimeOffset(2025, 12, 25, 12, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(christmasDay);

            AlternateScheduleSelector
                .GetScheduleForDate([december, christmasDay], new DateTimeOffset(2025, 12, 20, 12, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(december);
        }

        [Test]
        public void Should_Break_Priority_Ties_By_Definition_Order()
        {
            var first = new YamlPlayoutScheduleItem
            {
                Name = "First",
                StartDate = "04-01",
                EndDate = "04-30",
                Priority = 5
            };

            var second = new YamlPlayoutScheduleItem
            {
                Name = "Second",
                StartDate = "04-01",
                EndDate = "04-30",
                Priority = 5
            };

            AlternateScheduleSelector
                .GetScheduleForDate([first, second], new DateTimeOffset(2025, 4, 15, 12, 0, 0, Offset))
                .IfNone(() => null).ShouldBe(first);
        }

        [Test]
        public void Should_Not_Match_Invalid_Or_Missing_Dates()
        {
            var invalid = new YamlPlayoutScheduleItem
            {
                Name = "Invalid",
                StartDate = "not-a-date",
                EndDate = "04-30"
            };

            AlternateScheduleSelector
                .GetScheduleForDate([invalid], new DateTimeOffset(2025, 4, 15, 12, 0, 0, Offset))
                .IsNone.ShouldBeTrue();
        }
    }
}

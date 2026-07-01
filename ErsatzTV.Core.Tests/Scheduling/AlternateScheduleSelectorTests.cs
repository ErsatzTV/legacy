using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Scheduling;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

public static class AlternateScheduleSelectorTests
{
    [TestFixture]
    public class GetScheduleForDate
    {
        private static readonly TimeSpan Offset = TimeSpan.FromHours(-5);

        [Test]
        public void LimitToDateRange_Before_Start_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                EndMonth = 6,
                EndDay = 15
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 3, 31, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Before_Start_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                StartYear = 2024,
                EndMonth = 6,
                EndDay = 15,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 3, 31, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_On_Start_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                EndMonth = 6,
                EndDay = 15
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_On_Start_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                StartYear = 2024,
                EndMonth = 6,
                EndDay = 15,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_In_Range_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                EndMonth = 6,
                EndDay = 15
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 20, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_In_Range_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                StartYear = 2024,
                EndMonth = 6,
                EndDay = 15,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 20, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_On_End_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                EndMonth = 6,
                EndDay = 15
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 15, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_On_End_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                StartYear = 2023,
                EndMonth = 6,
                EndDay = 15,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 15, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_After_End_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                EndMonth = 6,
                EndDay = 15
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 16, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_After_End_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 4,
                StartDay = 1,
                StartYear = 2024,
                EndMonth = 6,
                EndDay = 15,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 16, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Before_Invalid_Start_Date_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                EndMonth = 6,
                EndDay = 15
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Before_Invalid_Start_Date_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                StartYear = 2023,
                EndMonth = 6,
                EndDay = 15,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_After_Invalid_Start_Date_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                EndMonth = 6,
                EndDay = 15
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_After_Invalid_Start_Date_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                StartYear = 2023,
                EndMonth = 6,
                EndDay = 15,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Before_Invalid_End_Date_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 1,
                StartDay = 1,
                EndMonth = 2,
                EndDay = 29
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Before_Invalid_End_Date_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 1,
                StartDay = 1,
                StartYear = 2023,
                EndMonth = 2,
                EndDay = 29,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_After_Invalid_End_Date_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 1,
                StartDay = 1,
                EndMonth = 2,
                EndDay = 29
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_After_Invalid_End_Date_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 1,
                StartDay = 1,
                StartYear = 2023,
                EndMonth = 2,
                EndDay = 29,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_Before_Start_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                EndMonth = 4,
                EndDay = 1
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 14, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_Before_Start_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                StartYear = 2023,
                EndMonth = 4,
                EndDay = 1,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 14, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_On_Start_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                EndMonth = 4,
                EndDay = 1
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 15, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_On_Start_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                StartYear = 2024,
                EndMonth = 4,
                EndDay = 1,
                EndYear = 2025
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 6, 15, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_In_Range_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                EndMonth = 4,
                EndDay = 1
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 7, 20, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_In_Range_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                StartYear = 2024,
                EndMonth = 4,
                EndDay = 1,
                EndYear = 2025
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 7, 20, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_On_End_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                EndMonth = 4,
                EndDay = 1
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_On_End_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                StartYear = 2023,
                EndMonth = 4,
                EndDay = 1,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_After_End_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                EndMonth = 4,
                EndDay = 1
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 2, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_After_End_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 6,
                StartDay = 15,
                StartYear = 2023,
                EndMonth = 4,
                EndDay = 1,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2024, 4, 2, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_Before_Invalid_Start_Date_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                EndMonth = 1,
                EndDay = 1
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_Before_Invalid_Start_Date_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                StartYear = 2023,
                EndMonth = 1,
                EndDay = 1,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_After_Invalid_Start_Date_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                EndMonth = 1,
                EndDay = 1
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_After_Invalid_Start_Date_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 2,
                StartDay = 29,
                StartYear = 2023,
                EndMonth = 1,
                EndDay = 1,
                EndYear = 2024
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_Before_Invalid_End_Date_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 5,
                StartDay = 1,
                EndMonth = 2,
                EndDay = 29
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_Before_Invalid_End_Date_With_Year_Should_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 5,
                StartDay = 1,
                StartYear = 2022,
                EndMonth = 2,
                EndDay = 29,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 2, 28, 0, 0, 0, Offset));

            result.IsSome.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_After_Invalid_End_Date_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 5,
                StartDay = 1,
                EndMonth = 2,
                EndDay = 29
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_Wrap_Around_After_Invalid_End_Date_With_Year_Should_Not_Return_Template()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 5,
                StartDay = 1,
                StartYear = 2022,
                EndMonth = 2,
                EndDay = 29,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_December_32nd_Should_Not_Crash()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 12,
                StartDay = 32,
                StartYear = 2022,
                EndMonth = 12,
                EndDay = 32,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsNone.ShouldBeFalse();
        }

        [Test]
        public void LimitToDateRange_13th_Month_Should_Not_Crash()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 13,
                StartDay = 30,
                StartYear = 2022,
                EndMonth = 13,
                EndDay = 30,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }

        [Test]
        public void LimitToDateRange_0th_Day_Should_Not_Crash()
        {
            var template = new PlayoutTemplate
            {
                DaysOfWeek = AlternateScheduleSelector.AllDaysOfWeek(),
                DaysOfMonth = AlternateScheduleSelector.AllDaysOfMonth(),
                MonthsOfYear = AlternateScheduleSelector.AllMonthsOfYear(),
                LimitToDateRange = true,
                StartMonth = 12,
                StartDay = 0,
                StartYear = 2022,
                EndMonth = 12,
                EndDay = 0,
                EndYear = 2023
            };

            Option<PlayoutTemplate> result = AlternateScheduleSelector.GetScheduleForDate(
                new List<PlayoutTemplate> { template },
                new DateTimeOffset(2023, 3, 1, 0, 0, 0, Offset));

            result.IsNone.ShouldBeTrue();
        }
    }
}

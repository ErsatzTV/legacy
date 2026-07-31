using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling.ClassicScheduling;

public class GetStartTimeAfterTests
{
    [Test]
    [Ignore("This test isn't ready to run yet")]
    public void Should_Return_Correct_Time_On_Dst_Fall_Back()
    {
        var scheduleItem = new ProgramScheduleItemOne
        {
            StartTime = TimeSpan.FromHours(3)
        };

        var state = new PlayoutBuilderState(
            0,
            null,
            Option<int>.None,
            Option<DateTimeOffset>.None,
            false,
            false,
            0,
            DateTimeOffset.Parse("2025-11-02T00:00:00-05:00"));

        DateTimeOffset result =
            PlayoutModeSchedulerBase<ProgramScheduleItem>.GetStartTimeAfter(state, scheduleItem, Option<ILogger>.None);

        result.ShouldBe(DateTimeOffset.Parse("2025-11-02T02:00:00-06:00"));
    }

    [Test]
    public void Should_Return_Current_Time_With_Flexible_Fixed_Start()
    {
        // 12:05 am, five minutes before the current time
        DateTimeOffset currentTime = Local(2025, 8, 29, 0, 10, 0);

        DateTimeOffset result = FlexibleStartTimeAfter(TimeSpan.FromMinutes(5), currentTime);

        result.ShouldBe(currentTime);
    }

    [Test]
    public void Flexible_Should_Wait_For_Midnight_Start_Time_Just_Before_Midnight()
    {
        DateTimeOffset result = FlexibleStartTimeAfter(TimeSpan.Zero, Local(2026, 7, 30, 23, 54, 58));

        result.ShouldBe(Local(2026, 7, 31, 0, 0, 0));
    }

    [Test]
    public void Flexible_Should_Wait_For_Early_Morning_Start_Time_Just_Before_Midnight()
    {
        DateTimeOffset result = FlexibleStartTimeAfter(
            TimeSpan.FromMinutes(30),
            Local(2026, 7, 30, 23, 50, 0));

        result.ShouldBe(Local(2026, 7, 31, 0, 30, 0));
    }

    [Test]
    public void Flexible_Should_Wait_Hours_For_A_Midnight_Start_Time()
    {
        // the nearest occurrence of midnight is an hour and a half ahead, not the one 22:30 behind
        DateTimeOffset result = FlexibleStartTimeAfter(TimeSpan.Zero, Local(2026, 7, 30, 22, 30, 0));

        result.ShouldBe(Local(2026, 7, 31, 0, 0, 0));
    }

    [Test]
    public void Flexible_Should_Not_Wait_A_Day_For_Midnight_Start_Time_After_Midnight()
    {
        DateTimeOffset currentTime = Local(2026, 7, 31, 0, 20, 1);

        DateTimeOffset result = FlexibleStartTimeAfter(TimeSpan.Zero, currentTime);

        result.ShouldBe(currentTime);
    }

    [Test]
    public void Flexible_Should_Not_Wait_A_Day_For_Start_Time_That_Just_Passed()
    {
        DateTimeOffset currentTime = Local(2026, 7, 31, 6, 10, 0);

        DateTimeOffset result = FlexibleStartTimeAfter(TimeSpan.FromHours(6), currentTime);

        result.ShouldBe(currentTime);
    }

    [Test]
    public void Flexible_Should_Wait_For_Later_Start_Time_On_The_Same_Day()
    {
        DateTimeOffset result = FlexibleStartTimeAfter(TimeSpan.FromHours(18), Local(2026, 7, 31, 12, 0, 0));

        result.ShouldBe(Local(2026, 7, 31, 18, 0, 0));
    }

    [Test]
    public void Flexible_Should_Not_Wait_A_Day_For_A_Late_Start_Time_Just_After_Midnight()
    {
        // the start time we missed two minutes ago is yesterday's, so this waits nearly a full day
        // to reach the next one; flexible starts immediately instead, even though the start time is
        // later on the same calendar day
        DateTimeOffset currentTime = Local(2026, 7, 31, 0, 1, 0);

        DateTimeOffset result = FlexibleStartTimeAfter(new TimeSpan(23, 59, 0), currentTime);

        result.ShouldBe(currentTime);
    }

    [Test]
    public void Flexible_Should_Wait_For_Midnight_Start_Time_On_Every_Day_Of_The_Year()
    {
        // waiting ten minutes for midnight has to land on midnight on every day, including the day
        // of a DST change, when tomorrow's offset differs from today's
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var failures = new List<string>();

        while (date.Year == 2026)
        {
            DateTime midnight = date.AddDays(1);

            // a zone that changes offset at midnight has no 12:00 am to wait for
            if (!TimeZoneInfo.Local.IsInvalidTime(midnight) && !TimeZoneInfo.Local.IsAmbiguousTime(midnight))
            {
                DateTimeOffset currentTime = Local(date.Year, date.Month, date.Day, 23, 50, 0);
                DateTimeOffset result = FlexibleStartTimeAfter(TimeSpan.Zero, currentTime);

                if (result.DateTime != midnight)
                {
                    failures.Add($"{currentTime:yyyy-MM-dd HH:mm zzz} -> {result:yyyy-MM-dd HH:mm zzz}");
                }
            }

            date = date.AddDays(1);
        }

        failures.ShouldBeEmpty($"[tz {TimeZoneInfo.Local.Id}] did not wait for midnight");
    }

    private static DateTimeOffset FlexibleStartTimeAfter(TimeSpan itemStartTime, DateTimeOffset currentTime)
    {
        var scheduleItem = new ProgramScheduleItemOne
        {
            StartTime = itemStartTime,
            FixedStartTimeBehavior = null,
            ProgramSchedule = new ProgramSchedule
            {
                FixedStartTimeBehavior = FixedStartTimeBehavior.Flexible
            }
        };

        var state = new PlayoutBuilderState(
            0,
            null,
            Option<int>.None,
            Option<DateTimeOffset>.None,
            false,
            false,
            0,
            currentTime);

        return PlayoutModeSchedulerBase<ProgramScheduleItem>.GetStartTimeAfter(
            state,
            scheduleItem,
            Option<ILogger>.None);
    }

    // these tests describe local wall clock behavior, so they have to be built from the local time zone
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute, int second)
    {
        var unspecified = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZoneInfo.Local.GetUtcOffset(unspecified));
    }
}

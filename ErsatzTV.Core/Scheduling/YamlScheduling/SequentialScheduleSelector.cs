using System.Globalization;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;

namespace ErsatzTV.Core.Scheduling.YamlScheduling;

public static class SequentialScheduleSelector
{
    private static readonly string[] SpecificFormats = ["yyyy-MM-dd"];
    private static readonly string[] AnnualFormats = ["MM-dd"];

    /// <summary>
    /// Returns the highest-priority schedule whose date range contains the given time.
    /// Ties on priority are broken by definition order (earlier wins).
    /// </summary>
    public static Option<YamlPlayoutScheduleItem> GetActiveSchedule(
        IReadOnlyList<YamlPlayoutScheduleItem> schedules,
        DateTimeOffset time)
    {
        if (schedules is null || schedules.Count == 0)
        {
            return Option<YamlPlayoutScheduleItem>.None;
        }

        var date = DateOnly.FromDateTime(time.LocalDateTime);

        Option<YamlPlayoutScheduleItem> best = Option<YamlPlayoutScheduleItem>.None;
        int bestPriority = int.MinValue;

        for (var i = 0; i < schedules.Count; i++)
        {
            YamlPlayoutScheduleItem schedule = schedules[i];
            if (!Matches(schedule, date))
            {
                continue;
            }

            // strictly greater keeps the earliest schedule on ties
            if (schedule.Priority > bestPriority)
            {
                bestPriority = schedule.Priority;
                best = schedule;
            }
        }

        return best;
    }

    private static bool Matches(YamlPlayoutScheduleItem schedule, DateOnly date)
    {
        ParsedDate? start = ParseDate(schedule.StartDate);
        ParsedDate? end = ParseDate(schedule.EndDate);
        if (start is null || end is null)
        {
            return false;
        }

        // a date is "specific" if either endpoint includes a year
        bool specific = start.Value.Year.HasValue || end.Value.Year.HasValue;
        if (specific)
        {
            int startYear = start.Value.Year ?? end.Value.Year ?? date.Year;
            int endYear = end.Value.Year ?? start.Value.Year ?? date.Year;

            try
            {
                var startDate = new DateOnly(startYear, start.Value.Month, start.Value.Day);
                var endDate = new DateOnly(endYear, end.Value.Month, end.Value.Day);
                return date >= startDate && date <= endDate;
            }
            catch (ArgumentOutOfRangeException)
            {
                // e.g. Feb 29 in a non-leap year
                return false;
            }
        }

        // annual range: compare by month/day so it repeats every year
        (int Month, int Day) startMd = (start.Value.Month, start.Value.Day);
        (int Month, int Day) endMd = (end.Value.Month, end.Value.Day);
        (int Month, int Day) currentMd = (date.Month, date.Day);

        if (CompareMonthDay(startMd, endMd) <= 0)
        {
            // range does not wrap the year boundary
            return CompareMonthDay(currentMd, startMd) >= 0 && CompareMonthDay(currentMd, endMd) <= 0;
        }

        // range wraps the year boundary (e.g. 12-01 through 01-15)
        return CompareMonthDay(currentMd, startMd) >= 0 || CompareMonthDay(currentMd, endMd) <= 0;
    }

    private static int CompareMonthDay((int Month, int Day) left, (int Month, int Day) right)
    {
        int monthComparison = left.Month.CompareTo(right.Month);
        return monthComparison != 0 ? monthComparison : left.Day.CompareTo(right.Day);
    }

    private static ParsedDate? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();

        if (DateOnly.TryParseExact(value, SpecificFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly specific))
        {
            return new ParsedDate(specific.Year, specific.Month, specific.Day);
        }

        if (DateOnly.TryParseExact(value, AnnualFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly annual))
        {
            return new ParsedDate(null, annual.Month, annual.Day);
        }

        return null;
    }

    private readonly record struct ParsedDate(int? Year, int Month, int Day);
}

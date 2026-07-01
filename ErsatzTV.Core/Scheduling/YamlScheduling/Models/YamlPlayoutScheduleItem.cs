using System.Globalization;
using ErsatzTV.Core.Domain.Scheduling;
using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Models;

public class YamlPlayoutScheduleItem : IAlternateScheduleItem
{
    public string Name { get; set; }

    [YamlMember(Alias = "start_date", ApplyNamingConventions = false)]
    public string StartDate { get; set; }

    [YamlMember(Alias = "end_date", ApplyNamingConventions = false)]
    public string EndDate { get; set; }

    public int Priority { get; set; }

    public List<YamlPlayoutInstruction> Playout { get; set; } = [];

    [YamlIgnore]
    public int Index => -Priority;

    // schedules are purely date-range based, so every day/month is eligible
    [YamlIgnore]
    public ICollection<DayOfWeek> DaysOfWeek => AlternateScheduleSelector.AllDaysOfWeek();

    [YamlIgnore]
    public ICollection<int> DaysOfMonth => AlternateScheduleSelector.AllDaysOfMonth();

    [YamlIgnore]
    public ICollection<int> MonthsOfYear => AlternateScheduleSelector.AllMonthsOfYear();

    [YamlIgnore]
    public bool LimitToDateRange => true;

    [YamlIgnore]
    public int StartMonth => Range.StartMonth;

    [YamlIgnore]
    public int StartDay => Range.StartDay;

    [YamlIgnore]
    public int? StartYear => Range.StartYear;

    [YamlIgnore]
    public int EndMonth => Range.EndMonth;

    [YamlIgnore]
    public int EndDay => Range.EndDay;

    [YamlIgnore]
    public int? EndYear => Range.EndYear;

    private NormalizedRange Range => NormalizedRange.From(StartDate, EndDate);

    private readonly record struct NormalizedRange(
        int StartMonth,
        int StartDay,
        int? StartYear,
        int EndMonth,
        int EndDay,
        int? EndYear)
    {
        // an impossible specific-year range (year 1) so an invalid schedule never matches a real date
        private static readonly NormalizedRange Invalid = new(1, 1, 1, 1, 1, 1);

        public static NormalizedRange From(string startValue, string endValue)
        {
            ParsedDate start = ParsedDate.Parse(startValue);
            ParsedDate end = ParsedDate.Parse(endValue);

            if (!start.Valid || !end.Valid)
            {
                return Invalid;
            }

            // a specific-year range requires a year on both endpoints, otherwise it repeats annually
            if (start.Year.HasValue && end.Year.HasValue)
            {
                return new NormalizedRange(start.Month, start.Day, start.Year, end.Month, end.Day, end.Year);
            }

            return new NormalizedRange(start.Month, start.Day, null, end.Month, end.Day, null);
        }
    }

    private readonly record struct ParsedDate(bool Valid, int? Year, int Month, int Day)
    {
        public static ParsedDate Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return default;
            }

            if (DateOnly.TryParseExact(
                    value.Trim(),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly specific))
            {
                return new ParsedDate(true, specific.Year, specific.Month, specific.Day);
            }

            string[] parts = value.Trim().Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int month)
                && int.TryParse(parts[1], out int day))
            {
                return new ParsedDate(true, null, month, day);
            }

            return default;
        }
    }
}

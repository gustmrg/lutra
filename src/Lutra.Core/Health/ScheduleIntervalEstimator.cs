using System.Text.RegularExpressions;

namespace Lutra.Core.Health;

public static partial class ScheduleIntervalEstimator
{
    public static double? EstimateIntervalHours(string schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return null;

        var trimmed = schedule.Trim();

        if (trimmed.Equals("daily", StringComparison.OrdinalIgnoreCase))
            return 24;

        if (trimmed.Equals("weekly", StringComparison.OrdinalIgnoreCase))
            return 168;

        if (trimmed.Equals("monthly", StringComparison.OrdinalIgnoreCase))
            return 720;

        // "DayOfWeek *-*-* HH:MM:SS" → weekly
        var weeklyMatch = WeeklyPattern().Match(trimmed);
        if (weeklyMatch.Success)
            return 168;

        // "*-*-* 00/N:00:00" → every N hours
        var hourlyMatch = HourlyRepeatPattern().Match(trimmed);
        if (hourlyMatch.Success && int.TryParse(hourlyMatch.Groups[1].Value, out var hours))
            return hours;

        // "*-*-* HH:MM:SS" → daily
        var dailyMatch = DailyPattern().Match(trimmed);
        if (dailyMatch.Success)
            return 24;

        // "*-*-DD HH:MM:SS" → monthly (specific day of month)
        var monthlyMatch = MonthlyPattern().Match(trimmed);
        if (monthlyMatch.Success)
            return 720;

        return null;
    }

    [GeneratedRegex(@"^(Mon|Tue|Wed|Thu|Fri|Sat|Sun)\s+\*-\*-\*\s+\d{2}:\d{2}:\d{2}$", RegexOptions.IgnoreCase)]
    private static partial Regex WeeklyPattern();

    [GeneratedRegex(@"^\*-\*-\*\s+\*?0?/(\d+):\d{2}:\d{2}$")]
    private static partial Regex HourlyRepeatPattern();

    [GeneratedRegex(@"^\*-\*-\*\s+\d{2}:\d{2}:\d{2}$")]
    private static partial Regex DailyPattern();

    [GeneratedRegex(@"^\*-\*-\d{1,2}\s+\d{2}:\d{2}:\d{2}$")]
    private static partial Regex MonthlyPattern();
}

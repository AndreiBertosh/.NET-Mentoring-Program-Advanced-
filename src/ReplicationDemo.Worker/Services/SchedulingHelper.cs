namespace ReplicationDemo.Worker.Services;

/// <summary>
/// Computes the next scheduled run time for a job based on its <c>Frequency</c> and
/// <c>ExecutionTime</c> settings.
/// </summary>
public static class SchedulingHelper
{
    /// <summary>
    /// Returns the next UTC <see cref="DateTime"/> at which a job should run.
    /// </summary>
    /// <param name="frequency">One of: <c>Hourly</c>, <c>Daily</c>, <c>Weekly</c>, <c>Monthly</c>.</param>
    /// <param name="executionTime">Time-of-day offset within the chosen frequency.</param>
    public static DateTime ComputeNextRunTime(string frequency, TimeSpan executionTime)
    {
        var now = DateTime.UtcNow;
        var todayAtTime = now.Date.Add(executionTime);

        return frequency.ToUpperInvariant() switch
        {
            "HOURLY" => GetNextHourly(now),
            "DAILY" => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(1),
            "WEEKLY" => todayAtTime > now ? todayAtTime : todayAtTime.AddDays(7),
            "MONTHLY" => GetNextMonthly(now, executionTime),
            _ => now.AddHours(1) // safe fallback
        };
    }

    private static DateTime GetNextHourly(DateTime now)
        => new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);

    private static DateTime GetNextMonthly(DateTime now, TimeSpan executionTime)
    {
        // First day of current month at executionTime
        var thisMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).Add(executionTime);
        return thisMonth > now ? thisMonth : thisMonth.AddMonths(1);
    }
}

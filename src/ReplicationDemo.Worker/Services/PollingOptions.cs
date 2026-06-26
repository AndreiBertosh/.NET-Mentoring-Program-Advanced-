namespace ReplicationDemo.Worker.Services;

public sealed class PollingOptions
{
    public const string SectionName = "Polling";

    /// <summary>How often the orchestrator checks for pending schedules (seconds).</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>How often the reconciliation pass resets stuck Running schedules (minutes).</summary>
    public int ReconciliationIntervalMinutes { get; set; } = 15;

    /// <summary>A Running schedule older than this many minutes is considered stuck (minutes).</summary>
    public int StuckRunningTimeoutMinutes { get; set; } = 60;

    /// <summary>Maximum schedules dispatched in one parallel batch.</summary>
    public int BatchSize { get; set; } = 50;
}

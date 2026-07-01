namespace ReplicationDemo.Worker.Services;

/// <summary>
/// Configuration for Job Runner custom metrics.
/// Bound from the "JobRunnerMetrics" configuration section.
/// </summary>
public sealed class JobRunnerMetricsOptions
{
    public const string SectionName = "JobRunnerMetrics";

    /// <summary>
    /// Execution duration (in milliseconds) at or above which a task is counted
    /// as "long-running" via the <c>LongRunningTasksCount</c> metric.
    /// In production set this to 30000 (>30s) or 60000 (>1 min).
    /// </summary>
    public int LongRunningThresholdMs { get; set; } = 2000;
}

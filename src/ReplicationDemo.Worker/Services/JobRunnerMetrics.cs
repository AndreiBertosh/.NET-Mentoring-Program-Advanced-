using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReplicationDemo.Worker.Services;

/// <summary>
/// Records Job Runner custom metrics. Implementations MUST be non-blocking and
/// MUST NOT throw — telemetry can never affect task execution (Telemetry Resilience NFR).
/// </summary>
public interface IJobRunnerMetrics
{
    /// <summary>
    /// Records the outcome of a single job execution.
    /// </summary>
    /// <param name="status">Low-cardinality outcome, e.g. "Succeeded" / "Failed".</param>
    /// <param name="durationMs">Wall-clock execution time in milliseconds.</param>
    void RecordExecution(string status, double durationMs);
}

/// <summary>
/// Application Insights implementation of <see cref="IJobRunnerMetrics"/>.
///
/// Uses <see cref="TelemetryClient.GetMetric(string, string)"/> which performs
/// client-side pre-aggregation and emits one aggregated data point per metric/series
/// every 60 seconds. This keeps the ingestion volume independent of throughput
/// (Efficient Aggregation NFR) and bounds cardinality to the single, stable
/// "Status" dimension (Metric Cardinality NFR — no JobId / UserId is ever used).
/// </summary>
public sealed class JobRunnerMetrics : IJobRunnerMetrics
{
    // Stable, low-cardinality metric names surfaced in Application Insights customMetrics.
    public const string TasksProcessed = "TasksProcessedCount";
    public const string FailedTasks = "FailedTasksCount";
    public const string LongRunningTasks = "LongRunningTasksCount";
    public const string ExecutionDuration = "JobExecutionDurationMs";

    private const string SucceededStatus = "Succeeded";
    private const string StatusDimension = "Status";

    private readonly Metric _tasksProcessed;
    private readonly Metric _failedTasks;
    private readonly Metric _longRunningTasks;
    private readonly Metric _executionDuration;
    private readonly JobRunnerMetricsOptions _options;
    private readonly ILogger<JobRunnerMetrics> _logger;

    public JobRunnerMetrics(
        TelemetryClient telemetryClient,
        IOptions<JobRunnerMetricsOptions> options,
        ILogger<JobRunnerMetrics> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Throughput, split by outcome → enables "TasksProcessedPerHour" via rate() on the dashboard.
        _tasksProcessed = telemetryClient.GetMetric(TasksProcessed, StatusDimension);

        // Absolute failure counter (no dimension — drives the failed-tasks alert).
        _failedTasks = telemetryClient.GetMetric(FailedTasks);

        // Long-running counter (no dimension — drives the long-running dashboard tile / alert).
        _longRunningTasks = telemetryClient.GetMetric(LongRunningTasks);

        // Duration distribution → p95/p99 percentiles on the dashboard, split by outcome.
        _executionDuration = telemetryClient.GetMetric(ExecutionDuration, StatusDimension);
    }

    public void RecordExecution(string status, double durationMs)
    {
        try
        {
            _tasksProcessed.TrackValue(1, status);
            _executionDuration.TrackValue(durationMs, status);

            if (!string.Equals(status, SucceededStatus, StringComparison.OrdinalIgnoreCase))
                _failedTasks.TrackValue(1);

            if (durationMs >= _options.LongRunningThresholdMs)
                _longRunningTasks.TrackValue(1);
        }
        catch (Exception ex)
        {
            // Telemetry must never break job execution.
            _logger.LogWarning(ex, "Failed to record Job Runner metrics (status={Status}).", status);
        }
    }
}

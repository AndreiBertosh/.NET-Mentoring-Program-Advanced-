using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Application.Services;

/// <summary>
/// Business logic for UC2.1 — Job Orchestrator operations (schedule polling, execution recording, history).
/// Encapsulates all consistency decisions; callers are not aware of consistency levels.
/// </summary>
public interface IJobOrchestratorService
{
    /// <summary>
    /// UC2.1a — Returns all pending schedules due for execution.
    /// Consistency: <b>Eventual</b> — replica read. The Orchestrator polling interval
    /// (seconds–minutes) vastly exceeds the replication lag (sub-second), so a missed
    /// cycle has no business impact on coarse-grained job frequencies.
    /// </summary>
    Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(CancellationToken ct = default);

    /// <summary>
    /// UC2.1b — Creates a new job schedule on the primary.
    /// Consistency: <b>Strong write</b> to primary — schedule state is authoritative.
    /// </summary>
    Task<JobSchedule> ScheduleJobAsync(JobSchedule schedule, CancellationToken ct = default);

    /// <summary>
    /// UC2.1b — Records a job execution result on the primary.
    /// Consistency: <b>Strong write</b> to primary — execution audit trail must be durable.
    /// Subsequent reads of this record from the replica use Eventual consistency.
    /// </summary>
    Task<JobExecution> RecordExecutionAsync(JobExecution execution, CancellationToken ct = default);

    /// <summary>
    /// UC2.2 — Returns execution history for a job.
    /// Supply <paramref name="from"/> / <paramref name="to"/> (UTC) to enable partition
    /// elimination on <c>PF_JobExecutions_ByMonth</c>.
    /// Consistency: <b>Eventual</b> — monitoring dashboard; sub-second lag is imperceptible
    /// relative to job execution durations.
    /// </summary>
    Task<IReadOnlyList<JobExecution>> GetExecutionsByJobIdAsync(
        Guid jobId,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default);
}

using ReplicationDemo.Domain.Consistency;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Domain.Repositories;

public interface IJobReadRepository
{
    /// <summary>
    /// Returns a job with its schedules and executions.
    /// Use <see cref="ConsistencyLevel.ReadAfterWrite"/> when the calling user may have
    /// just written this record and must see their own change immediately.
    /// </summary>
    Task<Job?> GetByIdAsync(Guid id, ConsistencyLevel consistency = ConsistencyLevel.Eventual, CancellationToken ct = default);

    /// <summary>
    /// Returns all jobs ordered by name.
    /// General catalog browsing uses <see cref="ConsistencyLevel.Eventual"/> (replica read).
    /// </summary>
    Task<IReadOnlyList<Job>> GetAllAsync(ConsistencyLevel consistency = ConsistencyLevel.Eventual, CancellationToken ct = default);

    /// <summary>
    /// Returns pending schedules due for execution.
    /// Uses <see cref="ConsistencyLevel.Eventual"/>: polling interval exceeds replication lag.
    /// </summary>
    Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(ConsistencyLevel consistency = ConsistencyLevel.Eventual, CancellationToken ct = default);

    /// <summary>
    /// Returns executions for <paramref name="jobId"/>.
    /// Supply <paramref name="from"/> and/or <paramref name="to"/> (UTC, exclusive upper bound)
    /// as the <c>StartedAt</c> partition key to enable partition elimination on
    /// <c>PF_JobExecutions_ByMonth</c> and avoid a full-table scan.
    /// Uses <see cref="ConsistencyLevel.Eventual"/> for monitoring/history dashboards.
    /// </summary>
    Task<IReadOnlyList<JobExecution>> GetExecutionsByJobIdAsync(
        Guid jobId,
        DateTime? from = null,
        DateTime? to = null,
        ConsistencyLevel consistency = ConsistencyLevel.Eventual,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if any <c>Pending</c> schedule exists for <paramref name="jobId"/>
    /// (regardless of <c>NextRunTime</c>). Used by the Orchestrator to prevent duplicate schedules.
    /// Always reads from the primary (Strong) to get the most current state.
    /// </summary>
    Task<bool> HasPendingScheduleAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// UC2.3 — Returns a single page of executions for <paramref name="jobId"/> plus the total
    /// matching count. Supply <paramref name="from"/>/<paramref name="to"/> (UTC, exclusive upper
    /// bound) to enable partition elimination on <c>PF_JobExecutions_ByMonth</c>.
    /// Uses <see cref="ConsistencyLevel.Eventual"/> (replica read) for the history dashboard.
    /// </summary>
    Task<(IReadOnlyList<JobExecution> Items, int TotalCount)> GetExecutionHistoryPagedAsync(
        Guid jobId,
        int page,
        int pageSize,
        DateTime? from = null,
        DateTime? to = null,
        ConsistencyLevel consistency = ConsistencyLevel.Eventual,
        CancellationToken ct = default);

    /// <summary>
    /// Returns schedules whose <c>Status</c> is <c>Running</c> and <c>CreatedAt</c> is before
    /// <paramref name="before"/> (UTC). Used by the reconciliation service to detect stuck executions.
    /// Always reads from the primary.
    /// </summary>
    Task<IReadOnlyList<JobSchedule>> GetStuckRunningSchedulesAsync(DateTime before, CancellationToken ct = default);
}

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
}

using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Domain.Repositories;

public interface IJobReadRepository
{
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns executions for <paramref name="jobId"/>.
    /// Supply <paramref name="from"/> and/or <paramref name="to"/> (UTC, exclusive upper bound)
    /// as the <c>StartedAt</c> partition key to enable partition elimination on
    /// <c>PF_JobExecutions_ByMonth</c> and avoid a full-table scan.
    /// </summary>
    Task<IReadOnlyList<JobExecution>> GetExecutionsByJobIdAsync(
        Guid jobId,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default);
}

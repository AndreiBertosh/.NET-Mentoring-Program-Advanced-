using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Domain.Repositories;

public interface IJobReadRepository
{
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<JobExecution>> GetExecutionsByJobIdAsync(Guid jobId, CancellationToken ct = default);
}

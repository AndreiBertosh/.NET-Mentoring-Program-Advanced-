using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Domain.Repositories;

public interface IJobWriteRepository
{
    Task<Job> CreateAsync(Job job, CancellationToken ct = default);
    Task UpdateAsync(Job job, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<JobSchedule> CreateScheduleAsync(JobSchedule schedule, CancellationToken ct = default);
    Task<JobExecution> CreateExecutionAsync(JobExecution execution, CancellationToken ct = default);
}

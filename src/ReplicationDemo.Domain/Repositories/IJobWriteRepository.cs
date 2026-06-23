using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Domain.Repositories;

public interface IJobWriteRepository
{
    Task<Job> CreateAsync(Job job, CancellationToken ct = default);
    Task UpdateAsync(Job job, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<JobSchedule> CreateScheduleAsync(JobSchedule schedule, CancellationToken ct = default);
    Task<JobExecution> CreateExecutionAsync(JobExecution execution, CancellationToken ct = default);

    /// <summary>Updates the <c>Status</c> field of a single <see cref="JobSchedule"/> row on the primary.</summary>
    Task UpdateScheduleStatusAsync(Guid scheduleId, string status, CancellationToken ct = default);

    /// <summary>Deletes all <c>Pending</c> schedules for a job (used when a job is updated or deleted).</summary>
    Task DeletePendingSchedulesByJobIdAsync(Guid jobId, CancellationToken ct = default);
}

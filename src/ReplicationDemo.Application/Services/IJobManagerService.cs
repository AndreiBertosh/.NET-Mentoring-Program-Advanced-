using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Application.Services;

/// <summary>
/// Business logic for UC1.1 — Job Manager operations (create, read, update, delete).
/// Encapsulates all consistency decisions; callers are not aware of consistency levels.
/// </summary>
public interface IJobManagerService
{
    /// <summary>
    /// UC1.1 — Creates a new job on the primary and tracks the write for ReadAfterWrite consistency.
    /// Consistency: <b>Strong write</b> to primary, then cooldown window starts for <paramref name="userId"/>.
    /// </summary>
    Task<Job> CreateJobAsync(Job job, string userId, CancellationToken ct = default);

    /// <summary>
    /// Returns all jobs ordered by name.
    /// Consistency: <b>Eventual</b> — replica read (catalog browsing).
    /// </summary>
    Task<IReadOnlyList<Job>> GetAllJobsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a job by ID with its schedules and executions.
    /// Consistency: <b>ReadAfterWrite</b> — primary if <paramref name="userId"/> is within
    /// the post-write cooldown window, replica otherwise.
    /// </summary>
    Task<Job?> GetJobByIdAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>
    /// UC1.2 — Updates an existing job and tracks the write for ReadAfterWrite consistency.
    /// Throws <see cref="InvalidOperationException"/> if the job is not found.
    /// Consistency: <b>Strong write</b> to primary.
    /// </summary>
    Task UpdateJobAsync(Job job, string userId, CancellationToken ct = default);

    /// <summary>
    /// UC1.3 — Deletes a job (cascades to schedules and executions) and tracks the write.
    /// Throws <see cref="InvalidOperationException"/> if the job is not found.
    /// Consistency: <b>Strong write</b> to primary.
    /// </summary>
    Task DeleteJobAsync(Guid id, string userId, CancellationToken ct = default);
}

using Microsoft.Extensions.Logging;
using ReplicationDemo.Application.Consistency;
using ReplicationDemo.Domain.Consistency;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.Application.Services;

/// <summary>
/// Implements <see cref="IJobManagerService"/> with consistency-aware data access.
///
/// <para>Consistency decisions (per Consistency-Requirements-Analysis.md):</para>
/// <list type="bullet">
///   <item><b>Writes</b> (Create/Update/Delete) → always go to the primary (Strong write).
///   The write result is used directly — no re-read from replica needed.</item>
///   <item><b>GetJobById</b> → ReadAfterWrite: primary during cooldown (so the user sees
///   their own changes immediately), replica once the cooldown expires.</item>
///   <item><b>GetAllJobs</b> → Eventual: catalog browsing from the replica is correct;
///   the list is not user-write-sensitive.</item>
/// </list>
/// </summary>
public sealed class JobManagerService : IJobManagerService
{
    private readonly IJobReadRepository _readRepo;
    private readonly IJobWriteRepository _writeRepo;
    private readonly ConsistencyManager _consistencyManager;
    private readonly ILogger<JobManagerService> _logger;

    public JobManagerService(
        IJobReadRepository readRepo,
        IJobWriteRepository writeRepo,
        ConsistencyManager consistencyManager,
        ILogger<JobManagerService> logger)
    {
        _readRepo = readRepo;
        _writeRepo = writeRepo;
        _consistencyManager = consistencyManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Job> CreateJobAsync(Job job, string userId, CancellationToken ct = default)
    {
        // UC1.1: Strong write to primary. The created entity is returned directly from the
        // write path — no replica read needed, so replication lag is irrelevant here.
        var created = await _writeRepo.CreateAsync(job, ct);

        // Start the ReadAfterWrite cooldown so that this user's next GET is routed to primary.
        _consistencyManager.TrackWrite(userId);

        _logger.LogInformation(
            "UC1.1 Job created: JobId={JobId} UserId={UserId} — ReadAfterWrite cooldown started",
            created.Id, userId);

        return created;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Job>> GetAllJobsAsync(CancellationToken ct = default)
    {
        // Catalog browsing: Eventual consistency.
        // Job definitions change infrequently; sub-second stale list is invisible to users.
        _logger.LogInformation("[CONSISTENCY] GetAllJobs — ConsistencyLevel=Eventual → replica");
        return await _readRepo.GetAllAsync(ConsistencyLevel.Eventual, ct);
    }

    /// <inheritdoc/>
    public async Task<Job?> GetJobByIdAsync(Guid id, string userId, CancellationToken ct = default)
    {
        // Resolve ReadAfterWrite: Strong (primary) if userId is within cooldown, Eventual otherwise.
        // This guarantees the user sees their own Create/Update immediately without requiring
        // global strong consistency for all clients.
        var effective = _consistencyManager.Resolve(userId, ConsistencyLevel.ReadAfterWrite);

        _logger.LogInformation(
            "[CONSISTENCY] GetJobById: JobId={JobId} UserId={UserId} → ConsistencyLevel={Consistency} → {Target}",
            id, userId, effective, effective == ConsistencyLevel.Strong ? "primary" : "replica");

        return await _readRepo.GetByIdAsync(id, effective, ct);
    }

    /// <inheritdoc/>
    public async Task UpdateJobAsync(Job job, string userId, CancellationToken ct = default)
    {
        // UC1.2: Strong write to primary.
        await _writeRepo.UpdateAsync(job, ct);

        _consistencyManager.TrackWrite(userId);

        _logger.LogInformation(
            "UC1.2 Job updated: JobId={JobId} UserId={UserId} — ReadAfterWrite cooldown started",
            job.Id, userId);
    }

    /// <inheritdoc/>
    public async Task DeleteJobAsync(Guid id, string userId, CancellationToken ct = default)
    {
        // UC1.3: Strong write to primary. Cascade deletes related schedules and executions.
        await _writeRepo.DeleteAsync(id, ct);

        _consistencyManager.TrackWrite(userId);

        _logger.LogInformation(
            "UC1.3 Job deleted: JobId={JobId} UserId={UserId} — ReadAfterWrite cooldown started",
            id, userId);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReplicationDemo.DAL.Contexts;
using ReplicationDemo.Domain.Consistency;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.DAL.Repositories;

public class JobReadRepository : IJobReadRepository
{
    private readonly IReadDbContext _replicaContext;
    private readonly PrimaryDbContext _primaryContext;
    private readonly ILogger<JobReadRepository> _logger;

    public JobReadRepository(
        IReadDbContext replicaContext,
        PrimaryDbContext primaryContext,
        ILogger<JobReadRepository> logger)
    {
        _replicaContext = replicaContext;
        _primaryContext = primaryContext;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the correct queryable sources based on the requested consistency level.
    /// <para>
    /// SQL Server database-native consistency is implemented by routing to the correct server:
    /// <list type="bullet">
    ///   <item><see cref="ConsistencyLevel.Strong"/> → Primary (port 1435): zero replication lag.</item>
    ///   <item><see cref="ConsistencyLevel.Eventual"/> → Replica (port 1434): sub-second lag via
    ///   Transactional Replication.</item>
    ///   <item><see cref="ConsistencyLevel.ReadAfterWrite"/> → resolved to Strong or Eventual by
    ///   <c>ConsistencyManager</c> before reaching the repository; treated as Eventual here
    ///   as a safe fallback.</item>
    /// </list>
    /// </para>
    /// </summary>
    private (IQueryable<Job> Jobs, IQueryable<JobSchedule> Schedules, IQueryable<JobExecution> Executions, string Target)
        ResolveQuerySources(ConsistencyLevel consistency)
    {
        if (consistency == ConsistencyLevel.Strong)
        {
            // Strong read: query the primary with AsNoTracking to avoid change-tracking overhead.
            return (
                _primaryContext.Jobs.AsNoTracking(),
                _primaryContext.JobSchedules.AsNoTracking(),
                _primaryContext.JobExecutions.AsNoTracking(),
                "primary");
        }

        // Eventual (and any unresolved ReadAfterWrite): query the replica.
        // ReadOnlyDbContext already applies AsNoTracking() internally.
        return (
            _replicaContext.Jobs,
            _replicaContext.JobSchedules,
            _replicaContext.JobExecutions,
            "replica");
    }

    public async Task<Job?> GetByIdAsync(Guid id, ConsistencyLevel consistency = ConsistencyLevel.Eventual, CancellationToken ct = default)
    {
        var (jobs, _, _, target) = ResolveQuerySources(consistency);

        _logger.LogInformation(
            "[DB-ROUTING] GetById: JobId={JobId} ConsistencyLevel={Consistency} → {Target}",
            id, consistency, target);

        return await jobs
            .Include(j => j.Schedules)
            .Include(j => j.Executions)
            .FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    public async Task<IReadOnlyList<Job>> GetAllAsync(ConsistencyLevel consistency = ConsistencyLevel.Eventual, CancellationToken ct = default)
    {
        var (jobs, _, _, target) = ResolveQuerySources(consistency);

        _logger.LogInformation(
            "[DB-ROUTING] GetAll: ConsistencyLevel={Consistency} → {Target}",
            consistency, target);

        return await jobs
            .OrderBy(j => j.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(ConsistencyLevel consistency = ConsistencyLevel.Eventual, CancellationToken ct = default)
    {
        var (_, schedules, _, target) = ResolveQuerySources(consistency);

        _logger.LogInformation(
            "[DB-ROUTING] GetPendingSchedules: ConsistencyLevel={Consistency} → {Target}",
            consistency, target);

        return await schedules
            .Include(s => s.Job)
            .Where(s => s.Status == "Pending" && s.NextRunTime <= DateTime.UtcNow)
            .OrderBy(s => s.NextRunTime)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobExecution>> GetExecutionsByJobIdAsync(
        Guid jobId,
        DateTime? from = null,
        DateTime? to = null,
        ConsistencyLevel consistency = ConsistencyLevel.Eventual,
        CancellationToken ct = default)
    {
        var (_, _, executions, target) = ResolveQuerySources(consistency);
        var query = executions.Where(e => e.JobId == jobId);

        if (from.HasValue)
            query = query.Where(e => e.StartedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(e => e.StartedAt < to.Value);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            if (from.HasValue || to.HasValue)
            {
                var (first, last) = PartitionHelper.GetPartitionRange(from, to);
                _logger.LogDebug(
                    "GetExecutionsByJobId: JobId={JobId} from={From} to={To} ConsistencyLevel={Consistency} → {Target} " +
                    "— partition key StartedAt applied, scanning partition(s) {First}–{Last} " +
                    "on PF_JobExecutions_ByMonth (partition elimination active)",
                    jobId, from, to, consistency, target, first, last);
            }
            else
            {
                _logger.LogDebug(
                    "GetExecutionsByJobId: JobId={JobId} ConsistencyLevel={Consistency} → {Target} " +
                    "— no partition key (StartedAt) supplied, all partitions of PF_JobExecutions_ByMonth will be scanned",
                    jobId, consistency, target);
            }
        }

        return await query
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);
    }

    public async Task<bool> HasPendingScheduleAsync(Guid jobId, CancellationToken ct = default)
    {
        // Always read from primary to get the most current state and avoid a race with
        // the JobLifecycleConsumer creating a duplicate schedule on at-least-once redelivery.
        return await _primaryContext.JobSchedules
            .AsNoTracking()
            .AnyAsync(s => s.JobId == jobId && s.Status == "Pending", ct);
    }

    public async Task<IReadOnlyList<JobSchedule>> GetStuckRunningSchedulesAsync(
        DateTime before,
        CancellationToken ct = default)
    {
        // Always read from primary — reconciliation needs authoritative state.
        return await _primaryContext.JobSchedules
            .AsNoTracking()
            .Where(s => s.Status == "Running" && s.CreatedAt < before)
            .ToListAsync(ct);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReplicationDemo.DAL.Contexts;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.DAL.Repositories;

public class JobReadRepository : IJobReadRepository
{
    private readonly IReadDbContext _context;
    private readonly ILogger<JobReadRepository> _logger;

    public JobReadRepository(IReadDbContext context, ILogger<JobReadRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        _logger.LogDebug(
            "GetById: JobId={JobId} — querying Jobs table (no partitioning on Jobs)",
            id);

        return await _context.Jobs
            .Include(j => j.Schedules)
            .Include(j => j.Executions)
            .FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    public async Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken ct = default)
    {
        _logger.LogDebug(
            "GetAll: querying all Jobs (no partitioning on Jobs)");

        return await _context.Jobs
            .OrderBy(j => j.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(CancellationToken ct = default)
    {
        _logger.LogDebug(
            "GetPendingSchedules: querying pending JobSchedules (no partitioning on JobSchedules)");

        return await _context.JobSchedules
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
        CancellationToken ct = default)
    {
        var query = _context.JobExecutions.Where(e => e.JobId == jobId);

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
                    "GetExecutionsByJobId: JobId={JobId} from={From} to={To} " +
                    "→ partition key StartedAt applied, scanning partition(s) {First}–{Last} " +
                    "on PF_JobExecutions_ByMonth (partition elimination active)",
                    jobId, from, to, first, last);
            }
            else
            {
                _logger.LogDebug(
                    "GetExecutionsByJobId: JobId={JobId} — no partition key (StartedAt) supplied, " +
                    "all partitions of PF_JobExecutions_ByMonth will be scanned",
                    jobId);
            }
        }

        return await query
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);
    }
}

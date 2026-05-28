using Microsoft.EntityFrameworkCore;
using ReplicationDemo.DAL.Contexts;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.DAL.Repositories;

public class JobReadRepository : IJobReadRepository
{
    private readonly IReadDbContext _context;

    public JobReadRepository(IReadDbContext context) => _context = context;

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Jobs
            .Include(j => j.Schedules)
            .Include(j => j.Executions)
            .FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    public async Task<IReadOnlyList<Job>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.Jobs
            .OrderBy(j => j.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(CancellationToken ct = default)
    {
        return await _context.JobSchedules
            .Include(s => s.Job)
            .Where(s => s.Status == "Pending" && s.NextRunTime <= DateTime.UtcNow)
            .OrderBy(s => s.NextRunTime)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<JobExecution>> GetExecutionsByJobIdAsync(Guid jobId, CancellationToken ct = default)
    {
        return await _context.JobExecutions
            .Where(e => e.JobId == jobId)
            .OrderByDescending(e => e.StartedAt)
            .ToListAsync(ct);
    }
}

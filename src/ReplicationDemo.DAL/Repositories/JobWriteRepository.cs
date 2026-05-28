using Microsoft.EntityFrameworkCore;
using ReplicationDemo.DAL.Contexts;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.DAL.Repositories;

public class JobWriteRepository : IJobWriteRepository
{
    private readonly IWriteDbContext _context;

    public JobWriteRepository(IWriteDbContext context) => _context = context;

    public async Task<Job> CreateAsync(Job job, CancellationToken ct = default)
    {
        job.CreatedAt = DateTime.UtcNow;
        _context.Jobs.Add(job);
        await _context.SaveChangesAsync(ct);
        return job;
    }

    public async Task UpdateAsync(Job job, CancellationToken ct = default)
    {
        var existing = await _context.Jobs.FindAsync([job.Id], ct)
            ?? throw new InvalidOperationException($"Job {job.Id} not found.");

        existing.Name = job.Name;
        existing.Frequency = job.Frequency;
        existing.ExecutionTime = job.ExecutionTime;
        existing.ApiEndpoint = job.ApiEndpoint;
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _context.Jobs.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Job {id} not found.");

        _context.Jobs.Remove(job);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<JobSchedule> CreateScheduleAsync(JobSchedule schedule, CancellationToken ct = default)
    {
        schedule.CreatedAt = DateTime.UtcNow;
        _context.JobSchedules.Add(schedule);
        await _context.SaveChangesAsync(ct);
        return schedule;
    }

    public async Task<JobExecution> CreateExecutionAsync(JobExecution execution, CancellationToken ct = default)
    {
        execution.CreatedAt = DateTime.UtcNow;
        _context.JobExecutions.Add(execution);
        await _context.SaveChangesAsync(ct);
        return execution;
    }
}

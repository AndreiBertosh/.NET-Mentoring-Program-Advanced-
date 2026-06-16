using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReplicationDemo.DAL.Contexts;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.DAL.Repositories;

public class JobWriteRepository : IJobWriteRepository
{
    private readonly IWriteDbContext _context;
    private readonly ILogger<JobWriteRepository> _logger;

    public JobWriteRepository(IWriteDbContext context, ILogger<JobWriteRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<Job> CreateAsync(Job job, CancellationToken ct = default)
    {
        job.CreatedAt = DateTime.UtcNow;
        _context.Jobs.Add(job);

        _logger.LogDebug(
            "CreateJob: JobId={JobId} Name={Name} — inserting into Jobs table (no partitioning on Jobs)",
            job.Id, job.Name);

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

        _logger.LogDebug(
            "UpdateJob: JobId={JobId} Name={Name} — updating Jobs table (no partitioning on Jobs)",
            job.Id, job.Name);

        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var job = await _context.Jobs.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Job {id} not found.");

        _logger.LogDebug(
            "DeleteJob: JobId={JobId} — removing Job; CASCADE will delete related JobExecutions " +
            "across all partitions of PF_JobExecutions_ByMonth (partition key StartedAt not used — full cascade)",
            id);

        _context.Jobs.Remove(job);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<JobSchedule> CreateScheduleAsync(JobSchedule schedule, CancellationToken ct = default)
    {
        schedule.CreatedAt = DateTime.UtcNow;
        _context.JobSchedules.Add(schedule);

        _logger.LogDebug(
            "CreateSchedule: ScheduleId={ScheduleId} JobId={JobId} NextRunTime={NextRunTime} " +
            "— inserting into JobSchedules table (no partitioning on JobSchedules)",
            schedule.Id, schedule.JobId, schedule.NextRunTime);

        await _context.SaveChangesAsync(ct);
        return schedule;
    }

    public async Task<JobExecution> CreateExecutionAsync(JobExecution execution, CancellationToken ct = default)
    {
        execution.CreatedAt = DateTime.UtcNow;
        _context.JobExecutions.Add(execution);

        var partition = PartitionHelper.GetPartitionNumber(execution.StartedAt);
        _logger.LogDebug(
            "CreateExecution: ExecutionId={ExecutionId} JobId={JobId} StartedAt={StartedAt} " +
            "→ partition key StartedAt routes row to partition {Partition} on PF_JobExecutions_ByMonth",
            execution.Id, execution.JobId, execution.StartedAt, partition);

        await _context.SaveChangesAsync(ct);
        return execution;
    }
}

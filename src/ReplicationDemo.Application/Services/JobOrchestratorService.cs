using Microsoft.Extensions.Logging;
using ReplicationDemo.Domain.Consistency;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.Application.Services;

/// <summary>
/// Implements <see cref="IJobOrchestratorService"/> with consistency-aware data access.
///
/// <para>Consistency decisions (per Consistency-Requirements-Analysis.md):</para>
/// <list type="bullet">
///   <item><b>GetPendingSchedules</b> → <see cref="ConsistencyLevel.Eventual"/>: replica read.
///   Polling interval >> replication lag; a missed poll cycle is harmless for coarse frequencies.</item>
///   <item><b>ScheduleJob / RecordExecution</b> → Strong write to primary.
///   Scheduling state and execution audit records must be durable and authoritative.</item>
///   <item><b>GetExecutionsByJobId</b> → <see cref="ConsistencyLevel.Eventual"/>: replica read.
///   History dashboard; sub-second lag is imperceptible relative to job runtimes.</item>
/// </list>
/// </summary>
public sealed class JobOrchestratorService : IJobOrchestratorService
{
    private readonly IJobReadRepository _readRepo;
    private readonly IJobWriteRepository _writeRepo;
    private readonly ILogger<JobOrchestratorService> _logger;

    public JobOrchestratorService(
        IJobReadRepository readRepo,
        IJobWriteRepository writeRepo,
        ILogger<JobOrchestratorService> logger)
    {
        _readRepo = readRepo;
        _writeRepo = writeRepo;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobSchedule>> GetPendingSchedulesAsync(CancellationToken ct = default)
    {
        // UC2.1a: Eventual consistency — replica read.
        // Replication lag is sub-second; Orchestrator polling interval is seconds-to-minutes.
        // A schedule appearing one poll cycle late has no business impact on Daily/Hourly frequencies.
        _logger.LogInformation("[CONSISTENCY] GetPendingSchedules — ConsistencyLevel=Eventual → replica");
        return await _readRepo.GetPendingSchedulesAsync(ConsistencyLevel.Eventual, ct);
    }

    /// <inheritdoc/>
    public async Task<JobSchedule> ScheduleJobAsync(JobSchedule schedule, CancellationToken ct = default)
    {
        // UC2.1b: Strong write to primary — scheduling state is authoritative truth.
        _logger.LogInformation(
            "[CONSISTENCY] ScheduleJob: JobId={JobId} NextRunTime={NextRunTime} — Strong write → primary",
            schedule.JobId, schedule.NextRunTime);
        return await _writeRepo.CreateScheduleAsync(schedule, ct);
    }

    /// <inheritdoc/>
    public async Task<JobExecution> RecordExecutionAsync(JobExecution execution, CancellationToken ct = default)
    {
        // Validate the job exists before attempting the insert to avoid a FK violation.
        // Use Strong consistency so we always check the primary (source of truth).
        var job = await _readRepo.GetByIdAsync(execution.JobId, ConsistencyLevel.Strong, ct);
        if (job is null)
            throw new InvalidOperationException($"Job {execution.JobId} not found.");

        // UC2.1b: Strong write to primary — execution audit trail must be durable.
        // Subsequent replica reads of this record use Eventual consistency (GetExecutionsByJobIdAsync).
        _logger.LogInformation(
            "[CONSISTENCY] RecordExecution: JobId={JobId} StartedAt={StartedAt} — Strong write → primary",
            execution.JobId, execution.StartedAt);
        return await _writeRepo.CreateExecutionAsync(execution, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobExecution>> GetExecutionsByJobIdAsync(
        Guid jobId,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        // UC2.2: Eventual consistency — monitoring/history dashboard, replica read.
        // Sub-second lag is imperceptible relative to job execution durations (seconds to minutes).
        _logger.LogInformation(
            "[CONSISTENCY] GetExecutionsByJobId: JobId={JobId} — ConsistencyLevel=Eventual → replica",
            jobId);
        return await _readRepo.GetExecutionsByJobIdAsync(jobId, from, to, ConsistencyLevel.Eventual, ct);
    }
}

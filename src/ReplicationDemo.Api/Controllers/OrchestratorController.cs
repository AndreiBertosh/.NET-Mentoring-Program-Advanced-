using Microsoft.AspNetCore.Mvc;
using ReplicationDemo.Application.Services;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Api.Controllers;

/// <summary>
/// Job Orchestrator API — UC2.1
/// Handles schedule polling, execution recording, and execution history queries with
/// consistency-aware data access. Consistency decisions are encapsulated in
/// <see cref="IJobOrchestratorService"/>; this controller does not expose consistency levels.
/// </summary>
[ApiController]
[Route("api/orchestrator")]
[Produces("application/json")]
public class OrchestratorController : ControllerBase
{
    private readonly IJobOrchestratorService _orchestrator;

    public OrchestratorController(IJobOrchestratorService orchestrator)
    {
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// UC2.1a — Returns all pending job schedules due for execution, ordered by next run time.
    /// Consistency: Eventual (replica read). The polling interval (seconds–minutes) greatly
    /// exceeds the replication lag (sub-second), so a one-cycle delay has no business impact.
    /// </summary>
    [HttpGet("schedules/pending")]
    public async Task<IActionResult> GetPendingSchedules(CancellationToken ct)
    {
        var schedules = await _orchestrator.GetPendingSchedulesAsync(ct);
        return Ok(schedules);
    }

    /// <summary>
    /// UC2.1b — Creates a new job schedule entry on the primary.
    /// Consistency: Strong write to primary — scheduling state is authoritative.
    /// </summary>
    [HttpPost("schedules")]
    public async Task<IActionResult> ScheduleJob([FromBody] ScheduleJobRequest request, CancellationToken ct)
    {
        var schedule = new JobSchedule
        {
            JobId = request.JobId,
            NextRunTime = request.NextRunTime,
            Status = "Pending"
        };

        var created = await _orchestrator.ScheduleJobAsync(schedule, ct);
        return CreatedAtAction(nameof(GetPendingSchedules), created);
    }

    /// <summary>
    /// UC2.1b — Records the result of a completed job execution on the primary.
    /// Consistency: Strong write to primary — execution audit trail must be durable.
    /// Subsequent reads of this record via <see cref="GetExecutions"/> use Eventual consistency.
    /// </summary>
    [HttpPost("executions")]
    public async Task<IActionResult> RecordExecution([FromBody] RecordExecutionRequest request, CancellationToken ct)
    {
        var execution = new JobExecution
        {
            JobId = request.JobId,
            StartedAt = request.StartedAt,
            FinishedAt = request.FinishedAt,
            Result = request.Result
        };

        try
        {
            var created = await _orchestrator.RecordExecutionAsync(execution, ct);
            return CreatedAtAction(nameof(GetExecutions), new { jobId = created.JobId }, created);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// UC2.2 — Returns execution history for a job, ordered by start time descending.
    /// Consistency: Eventual (replica read — monitoring dashboard).
    /// Optionally supply <paramref name="from"/> and/or <paramref name="to"/> (UTC) to restrict
    /// the result range and enable partition elimination on <c>PF_JobExecutions_ByMonth</c>.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="from">Optional UTC lower bound for <c>StartedAt</c> (partition key).</param>
    /// <param name="to">Optional UTC exclusive upper bound for <c>StartedAt</c> (partition key).</param>
    [HttpGet("jobs/{jobId:guid}/executions")]
    public async Task<IActionResult> GetExecutions(
        Guid jobId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var executions = await _orchestrator.GetExecutionsByJobIdAsync(jobId, from, to, ct);
        return Ok(executions);
    }
}

public record ScheduleJobRequest(Guid JobId, DateTime NextRunTime);
public record RecordExecutionRequest(Guid JobId, DateTime StartedAt, DateTime? FinishedAt, string Result);

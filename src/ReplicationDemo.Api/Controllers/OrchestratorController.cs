using Microsoft.AspNetCore.Mvc;
using ProtoBuf;
using ReplicationDemo.Api.Models;
using ReplicationDemo.Application.Services;
using ReplicationDemo.Domain.Entities;
using System.Diagnostics;
using System.Text.Json;

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

    // ── UC 2.3 — View Job Execution History ──────────────────────────────────────

    /// <summary>
    /// UC2.3 — Returns a paginated page of execution history for a job.
    /// Supports content negotiation: send <c>Accept: application/json</c> for JSON (default)
    /// or <c>Accept: application/x-protobuf</c> for binary Protobuf.
    /// Consistency: Eventual (replica read).
    /// Partition elimination is active when <paramref name="from"/>/<paramref name="to"/> are set.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    /// <param name="page">1-based page number (default: 1).</param>
    /// <param name="pageSize">Records per page, capped at 500 (default: 100).</param>
    /// <param name="from">Optional UTC lower bound for <c>StartedAt</c>.</param>
    /// <param name="to">Optional UTC exclusive upper bound for <c>StartedAt</c>.</param>
    [HttpGet("jobs/{jobId:guid}/executions/history")]
    [Produces("application/json", "application/x-protobuf")]
    public async Task<IActionResult> GetExecutionHistory(
        Guid jobId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var result = await _orchestrator.GetExecutionHistoryAsync(jobId, page, pageSize, from, to, ct);

        var response = new ExecutionHistoryResponse
        {
            JobId = jobId,
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
            Executions = result.Items.Select(static e => new JobExecutionDto
            {
                Id = e.Id,
                JobId = e.JobId,
                StartedAt = e.StartedAt,
                FinishedAt = e.FinishedAt,
                Result = e.Result
            }).ToList()
        };

        return Ok(response);
    }

    /// <summary>
    /// UC2.3 — In-process benchmark: measures JSON vs Protobuf payload size and serialisation
    /// time over <paramref name="iterations"/> iterations.
    /// Always returns JSON so the comparison is human-readable in the browser / Swagger UI.
    /// Use the numbers here to populate or validate <c>docs/comparison-report.md</c>.
    /// </summary>
    /// <param name="jobId">Job identifier whose executions are used as benchmark data.</param>
    /// <param name="from">Optional UTC lower bound (enables partition elimination).</param>
    /// <param name="to">Optional UTC exclusive upper bound.</param>
    /// <param name="iterations">Number of serialisation rounds (default: 20).</param>
    [HttpGet("jobs/{jobId:guid}/executions/benchmark")]
    [Produces("application/json")]
    public async Task<IActionResult> BenchmarkFormats(
        Guid jobId,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int iterations = 20,
        CancellationToken ct = default)
    {
        iterations = Math.Clamp(iterations, 1, 100);

        // Fetch up to 500 records — enough for a statistically meaningful measurement.
        var result = await _orchestrator.GetExecutionHistoryAsync(jobId, 1, 500, from, to, ct);

        var response = new ExecutionHistoryResponse
        {
            JobId = jobId,
            TotalCount = result.TotalCount,
            Page = 1,
            PageSize = result.Items.Count,
            Executions = result.Items.Select(static e => new JobExecutionDto
            {
                Id = e.Id,
                JobId = e.JobId,
                StartedAt = e.StartedAt,
                FinishedAt = e.FinishedAt,
                Result = e.Result
            }).ToList()
        };

        // ── JSON baseline ────────────────────────────────────────────────────────
        var jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Warm-up
        JsonSerializer.SerializeToUtf8Bytes(response, jsonOpts);

        var jsonSerTimes = new double[iterations];
        var jsonDeserTimes = new double[iterations];
        byte[] jsonBytes = [];

        for (int i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            jsonBytes = JsonSerializer.SerializeToUtf8Bytes(response, jsonOpts);
            jsonSerTimes[i] = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            JsonSerializer.Deserialize<ExecutionHistoryResponse>(jsonBytes, jsonOpts);
            jsonDeserTimes[i] = sw.Elapsed.TotalMilliseconds;
        }

        // ── Protobuf ─────────────────────────────────────────────────────────────
        // Warm-up
        using (var warmup = new MemoryStream())
            Serializer.Serialize(warmup, response);

        var pbSerTimes = new double[iterations];
        var pbDeserTimes = new double[iterations];
        byte[] pbBytes = [];

        for (int i = 0; i < iterations; i++)
        {
            using var ms = new MemoryStream();
            var sw = Stopwatch.StartNew();
            Serializer.Serialize(ms, response);
            pbSerTimes[i] = sw.Elapsed.TotalMilliseconds;
            pbBytes = ms.ToArray();

            using var readMs = new MemoryStream(pbBytes);
            sw.Restart();
            Serializer.Deserialize<ExecutionHistoryResponse>(readMs);
            pbDeserTimes[i] = sw.Elapsed.TotalMilliseconds;
        }

        double reductionPct = jsonBytes.Length > 0
            ? (1.0 - (double)pbBytes.Length / jsonBytes.Length) * 100.0
            : 0;

        string recommendation = reductionPct >= 30
            ? $"Protobuf — {reductionPct:F1}% smaller payload, faster serialisation. " +
              "Recommended for high-throughput scenarios (1 000+ concurrent readers)."
            : "JSON — payload difference below 30%; use JSON for broader client compatibility.";

        return Ok(new BenchmarkResult
        {
            RecordCount = response.Executions.Count,
            Iterations = iterations,
            Json = new BenchmarkResult.FormatStats
            {
                PayloadBytes = jsonBytes.Length,
                PayloadKb = $"{jsonBytes.Length / 1024.0:F2} KB",
                AvgSerializeMs = Math.Round(jsonSerTimes.Average(), 4),
                AvgDeserializeMs = Math.Round(jsonDeserTimes.Average(), 4)
            },
            Protobuf = new BenchmarkResult.FormatStats
            {
                PayloadBytes = pbBytes.Length,
                PayloadKb = $"{pbBytes.Length / 1024.0:F2} KB",
                AvgSerializeMs = Math.Round(pbSerTimes.Average(), 4),
                AvgDeserializeMs = Math.Round(pbDeserTimes.Average(), 4)
            },
            SizeReductionPercent = $"{reductionPct:F1}%",
            Recommendation = recommendation
        });
    }
}

public record ScheduleJobRequest(Guid JobId, DateTime NextRunTime);
public record RecordExecutionRequest(Guid JobId, DateTime StartedAt, DateTime? FinishedAt, string Result);

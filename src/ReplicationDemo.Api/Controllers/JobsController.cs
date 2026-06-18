using Microsoft.AspNetCore.Mvc;
using ReplicationDemo.Application.Services;
using ReplicationDemo.Domain.Entities;

namespace ReplicationDemo.Api.Controllers;

/// <summary>
/// Job Manager API — UC1.1
/// Handles job CRUD operations with consistency-aware data access.
/// Consistency decisions are encapsulated in <see cref="IJobManagerService"/>; this
/// controller does not specify or expose consistency levels to API consumers.
/// </summary>
[ApiController]
[Route("api/job-manager/jobs")]
[Produces("application/json")]
public class JobManagerController : ControllerBase
{
    private readonly IJobManagerService _jobManager;

    public JobManagerController(IJobManagerService jobManager)
    {
        _jobManager = jobManager;
    }

    /// <summary>
    /// Returns all jobs ordered by name.
    /// Consistency: Eventual (replica read — catalog browsing).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var jobs = await _jobManager.GetAllJobsAsync(ct);
        return Ok(jobs);
    }

    /// <summary>
    /// Returns a single job with its schedules and executions.
    /// Consistency: ReadAfterWrite — primary during post-write cooldown, replica otherwise.
    /// Pass <c>X-User-Id</c> header to correlate reads with your own writes.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var userId = ResolveUserId();
        var job = await _jobManager.GetJobByIdAsync(id, userId, ct);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>
    /// UC1.1 — Creates a new job.
    /// Consistency: Strong write to primary. ReadAfterWrite cooldown starts immediately
    /// so that a subsequent GET by the same user is routed to primary.
    /// Pass <c>X-User-Id</c> header to correlate this write with your subsequent reads.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest request, CancellationToken ct)
    {
        var userId = ResolveUserId();
        var job = new Job
        {
            Name = request.Name,
            Frequency = request.Frequency,
            ExecutionTime = request.ExecutionTime,
            ApiEndpoint = request.ApiEndpoint
        };

        var created = await _jobManager.CreateJobAsync(job, userId, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// UC1.2 — Updates an existing job.
    /// Consistency: Strong write to primary. ReadAfterWrite cooldown restarts for this user.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateJobRequest request, CancellationToken ct)
    {
        var userId = ResolveUserId();
        var job = new Job
        {
            Id = id,
            Name = request.Name,
            Frequency = request.Frequency,
            ExecutionTime = request.ExecutionTime,
            ApiEndpoint = request.ApiEndpoint
        };

        try
        {
            await _jobManager.UpdateJobAsync(job, userId, ct);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>
    /// UC1.3 — Deletes a job and all its associated schedules and executions.
    /// Consistency: Strong write to primary. ReadAfterWrite cooldown restarts for this user.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var userId = ResolveUserId();
        try
        {
            await _jobManager.DeleteJobAsync(id, userId, ct);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>
    /// Extracts a stable user identifier for ReadAfterWrite tracking.
    /// Checks <c>X-User-Id</c> header first, falls back to remote IP, then "anonymous".
    /// </summary>
    private string ResolveUserId()
    {
        if (HttpContext.Request.Headers.TryGetValue("X-User-Id", out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.ToString();
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }
}

public record CreateJobRequest(string Name, string Frequency, TimeSpan ExecutionTime, string ApiEndpoint);


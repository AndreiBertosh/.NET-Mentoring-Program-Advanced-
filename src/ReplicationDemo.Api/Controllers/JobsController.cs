using Microsoft.AspNetCore.Mvc;
using ReplicationDemo.Application.Services;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Messaging.Messages;
using ReplicationDemo.Messaging.Publishing;

namespace ReplicationDemo.Api.Controllers;

/// <summary>
/// Job Manager API — UC1.1
/// Handles job CRUD operations with consistency-aware data access.
/// After each mutating operation the controller publishes a job-lifecycle event to Azure Service Bus
/// (fire-and-forget after the DB write). Failures are logged but do not roll back the HTTP response —
/// a Transactional Outbox pattern should be used in production to guarantee delivery.
/// </summary>
[ApiController]
[Route("api/job-manager/jobs")]
[Produces("application/json")]
public class JobManagerController : ControllerBase
{
    private readonly IJobManagerService _jobManager;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<JobManagerController> _logger;

    public JobManagerController(
        IJobManagerService jobManager,
        IMessagePublisher publisher,
        ILogger<JobManagerController> logger)
    {
        _jobManager = jobManager;
        _publisher = publisher;
        _logger = logger;
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
    /// Consistency: Strong write to primary. ReadAfterWrite cooldown starts immediately.
    /// After the DB write, a <c>job.created</c> event is published to the <c>job-lifecycle</c> topic
    /// so the Job Orchestrator can create the initial schedule asynchronously.
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

        await PublishSafeAsync(() => _publisher.PublishJobCreatedAsync(new JobCreatedEvent(
            EventId: Guid.NewGuid().ToString(),
            OccurredAt: DateTime.UtcNow,
            CorrelationId: HttpContext.TraceIdentifier,
            Payload: new JobCreatedPayload(
                JobId: created.Id,
                Name: created.Name,
                Frequency: created.Frequency,
                ExecutionTime: created.ExecutionTime,
                ApiEndpoint: created.ApiEndpoint,
                CreatedAt: created.CreatedAt)), ct), created.Id);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>
    /// UC1.2 — Updates an existing job.
    /// Consistency: Strong write to primary. ReadAfterWrite cooldown restarts for this user.
    /// Publishes a <c>job.updated</c> event so the Orchestrator reschedules the job.
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

        await PublishSafeAsync(() => _publisher.PublishJobUpdatedAsync(new JobUpdatedEvent(
            EventId: Guid.NewGuid().ToString(),
            OccurredAt: DateTime.UtcNow,
            CorrelationId: HttpContext.TraceIdentifier,
            Payload: new JobUpdatedPayload(
                JobId: id,
                Name: request.Name,
                Frequency: request.Frequency,
                ExecutionTime: request.ExecutionTime,
                ApiEndpoint: request.ApiEndpoint,
                UpdatedAt: DateTime.UtcNow)), ct), id);

        return NoContent();
    }

    /// <summary>
    /// UC1.3 — Deletes a job and all its associated schedules and executions.
    /// Consistency: Strong write to primary. ReadAfterWrite cooldown restarts for this user.
    /// Publishes a <c>job.deleted</c> event so the Orchestrator can cancel in-flight executions.
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

        await PublishSafeAsync(() => _publisher.PublishJobDeletedAsync(new JobDeletedEvent(
            EventId: Guid.NewGuid().ToString(),
            OccurredAt: DateTime.UtcNow,
            CorrelationId: HttpContext.TraceIdentifier,
            Payload: new JobDeletedPayload(
                JobId: id,
                DeletedAt: DateTime.UtcNow)), ct), id);

        return NoContent();
    }

    /// <summary>
    /// Publishes an event to Service Bus without failing the HTTP response if publishing fails.
    /// Production code should use a Transactional Outbox to guarantee event delivery.
    /// </summary>
    private async Task PublishSafeAsync(Func<Task> publish, Guid jobId)
    {
        try
        {
            await publish();
        }

        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish Service Bus event for JobId={JobId}. " +
                "The job was already saved to the DB. Check Service Bus connectivity.",
                jobId);
        }
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


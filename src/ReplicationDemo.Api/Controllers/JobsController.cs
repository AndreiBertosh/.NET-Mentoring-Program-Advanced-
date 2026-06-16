using Microsoft.AspNetCore.Mvc;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly IJobReadRepository _readRepo;
    private readonly IJobWriteRepository _writeRepo;

    public JobsController(IJobReadRepository readRepo, IJobWriteRepository writeRepo)
    {
        _readRepo = readRepo;
        _writeRepo = writeRepo;
    }

    /// <summary>Reads from REPLICA — list all jobs.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var jobs = await _readRepo.GetAllAsync(ct);
        return Ok(jobs);
    }

    /// <summary>Reads from REPLICA — get job by id with schedules and executions.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var job = await _readRepo.GetByIdAsync(id, ct);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>Writes to PRIMARY — create a new job (UC1.1).</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest request, CancellationToken ct)
    {
        var job = new Job
        {
            Name = request.Name,
            Frequency = request.Frequency,
            ExecutionTime = request.ExecutionTime,
            ApiEndpoint = request.ApiEndpoint
        };

        var created = await _writeRepo.CreateAsync(job, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    /// <summary>Writes to PRIMARY — update a job.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateJobRequest request, CancellationToken ct)
    {
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
            await _writeRepo.UpdateAsync(job, ct);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>Writes to PRIMARY — delete a job.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _writeRepo.DeleteAsync(id, ct);
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>Reads from REPLICA — get pending schedules (UC2.1).</summary>
    [HttpGet("schedules/pending")]
    public async Task<IActionResult> GetPendingSchedules(CancellationToken ct)
    {
        var schedules = await _readRepo.GetPendingSchedulesAsync(ct);
        return Ok(schedules);
    }

    /// <summary>Reads from REPLICA — get execution history for a job.</summary>
    /// <param name="id">Job identifier.</param>
    /// <param name="from">Optional UTC lower bound for <c>StartedAt</c> (partition key). Enables partition elimination on PF_JobExecutions_ByMonth.</param>
    /// <param name="to">Optional UTC exclusive upper bound for <c>StartedAt</c> (partition key). Enables partition elimination on PF_JobExecutions_ByMonth.</param>
    [HttpGet("{id:guid}/executions")]
    public async Task<IActionResult> GetExecutions(
        Guid id,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        var executions = await _readRepo.GetExecutionsByJobIdAsync(id, from, to, ct);
        return Ok(executions);
    }
}

public record CreateJobRequest(string Name, string Frequency, TimeSpan ExecutionTime, string ApiEndpoint);

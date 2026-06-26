using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Domain.Consistency;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;
using ReplicationDemo.Messaging.Messages;
using ReplicationDemo.Messaging.Publishing;

namespace ReplicationDemo.Worker.Services;

/// <summary>
/// UC2.1a — Batch polling service.
/// Runs every <see cref="PollingOptions.IntervalSeconds"/> seconds, queries the replica for
/// pending schedules, marks each as <c>Running</c> on the primary, then dispatches a
/// <see cref="JobExecutionRequestMessage"/> to the <c>job-execution-requests</c> queue
/// (session key = JobId guarantees per-job ordering in the runner).
///
/// Also runs a reconciliation pass every <see cref="PollingOptions.ReconciliationIntervalMinutes"/>
/// minutes to reset schedules that have been stuck in <c>Running</c> beyond the configured timeout.
/// </summary>
public sealed class SchedulePollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagePublisher _publisher;
    private readonly PollingOptions _opts;
    private readonly ILogger<SchedulePollingService> _logger;

    private DateTime _lastReconciliationAt = DateTime.MinValue;

    public SchedulePollingService(
        IServiceScopeFactory scopeFactory,
        IMessagePublisher publisher,
        IOptions<PollingOptions> opts,
        ILogger<SchedulePollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _opts = opts.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SchedulePollingService started. Polling every {Interval}s, reconciliation every {Recon}min.",
            _opts.IntervalSeconds, _opts.ReconciliationIntervalMinutes);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_opts.IntervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await PollAndDispatchAsync(stoppingToken);

            if (DateTime.UtcNow - _lastReconciliationAt >
                TimeSpan.FromMinutes(_opts.ReconciliationIntervalMinutes))
            {
                await ReconcileStuckSchedulesAsync(stoppingToken);
                _lastReconciliationAt = DateTime.UtcNow;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Batch poll
    // -----------------------------------------------------------------------

    private async Task PollAndDispatchAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readRepo = scope.ServiceProvider.GetRequiredService<IJobReadRepository>();
            var writeRepo = scope.ServiceProvider.GetRequiredService<IJobWriteRepository>();

            // UC2.1a: Eventual consistency — replica read.
            var pending = await readRepo.GetPendingSchedulesAsync(ConsistencyLevel.Eventual, ct);

            if (pending.Count == 0)
            {
                _logger.LogDebug("Polling tick: no pending schedules.");
                return;
            }

            _logger.LogInformation("Polling tick: {Count} pending schedule(s) to dispatch.", pending.Count);

            // Fan out in parallel batches of BatchSize
            foreach (var batch in pending.Chunk(_opts.BatchSize))
            {
                var tasks = batch.Select(s => DispatchScheduleAsync(s, writeRepo, ct));
                await Task.WhenAll(tasks);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during schedule polling tick.");
        }
    }

    private async Task DispatchScheduleAsync(
        Domain.Entities.JobSchedule schedule,
        IJobWriteRepository writeRepo,
        CancellationToken ct)
    {
        try
        {
            // Mark as Running on the primary before enqueuing.
            // If the Service Bus publish fails, the reconciliation pass will reset it to Pending.
            await writeRepo.UpdateScheduleStatusAsync(schedule.Id, "Running", ct);

            var request = new JobExecutionRequestMessage(
                MessageId: $"exec-req-{schedule.JobId}-{schedule.Id}",
                SessionId: schedule.JobId.ToString(),
                Payload: new JobExecutionRequestPayload(
                    JobId: schedule.JobId,
                    ScheduleId: schedule.Id,
                    JobName: schedule.Job?.Name ?? "Unknown",
                    ApiEndpoint: schedule.Job?.ApiEndpoint ?? string.Empty,
                    ScheduledAt: schedule.NextRunTime,
                    TriggeredAt: DateTime.UtcNow));

            await _publisher.SendExecutionRequestAsync(request, ct);

            _logger.LogInformation(
                "Dispatched: JobId={JobId} ScheduleId={ScheduleId} ScheduledAt={ScheduledAt}",
                schedule.JobId, schedule.Id, schedule.NextRunTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to dispatch schedule ScheduleId={ScheduleId} JobId={JobId}. " +
                "Reconciliation will reset it to Pending after the timeout.",
                schedule.Id, schedule.JobId);
        }
    }

    // -----------------------------------------------------------------------
    // Reconciliation — reset stuck Running schedules
    // -----------------------------------------------------------------------

    private async Task ReconcileStuckSchedulesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readRepo = scope.ServiceProvider.GetRequiredService<IJobReadRepository>();
            var writeRepo = scope.ServiceProvider.GetRequiredService<IJobWriteRepository>();

            var cutoff = DateTime.UtcNow.AddMinutes(-_opts.StuckRunningTimeoutMinutes);
            var stuck = await readRepo.GetStuckRunningSchedulesAsync(cutoff, ct);

            foreach (var schedule in stuck)
            {
                _logger.LogWarning(
                    "Reconciliation: resetting stuck Running → Pending. " +
                    "ScheduleId={ScheduleId} JobId={JobId} CreatedAt={CreatedAt}",
                    schedule.Id, schedule.JobId, schedule.CreatedAt);

                await writeRepo.UpdateScheduleStatusAsync(schedule.Id, "Pending", ct);
            }

            if (stuck.Count > 0)
                _logger.LogInformation("Reconciliation complete: reset {Count} stuck schedule(s).", stuck.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error during reconciliation pass.");
        }
    }
}

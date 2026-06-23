using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Domain.Consistency;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;
using ReplicationDemo.Messaging;
using ReplicationDemo.Messaging.Messages;
using ReplicationDemo.Messaging.Publishing;
using ReplicationDemo.Worker.Services;

namespace ReplicationDemo.Worker.Consumers;

/// <summary>
/// UC2.1b — Execution result consumer.
/// Attempts to use session-enabled processor if the namespace supports it; falls back to
/// regular processor for Basic tier (no session support).
/// </summary>
public sealed class JobExecutionResultConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMessagePublisher _publisher;
    private readonly ILogger<JobExecutionResultConsumer> _logger;

    private ServiceBusSessionProcessor? _sessionProcessor;
    private ServiceBusProcessor? _regularProcessor;
    private bool _useSessions = false;

    public JobExecutionResultConsumer(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        IServiceScopeFactory scopeFactory,
        IMessagePublisher publisher,
        ILogger<JobExecutionResultConsumer> logger)
    {
        _client = client;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Try to detect if the namespace supports sessions
        _useSessions = await DetectSessionSupportAsync(ct);

        if (_useSessions)
        {
            _sessionProcessor = _client.CreateSessionProcessor(
                _options.Queues.ExecutionResults,
                new ServiceBusSessionProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentSessions = 4,
                    MaxConcurrentCallsPerSession = 1
                });

            _sessionProcessor.ProcessMessageAsync += ProcessSessionMessageAsync;
            _sessionProcessor.ProcessErrorAsync += ProcessErrorAsync;

            _logger.LogInformation("JobExecutionResultConsumer started (session-enabled) → {Queue}",
                _options.Queues.ExecutionResults);
            
            await _sessionProcessor.StartProcessingAsync(ct);
        }
        else
        {
            _regularProcessor = _client.CreateProcessor(
                _options.Queues.ExecutionResults,
                new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = 4
                });

            _regularProcessor.ProcessMessageAsync += ProcessRegularMessageAsync;
            _regularProcessor.ProcessErrorAsync += ProcessErrorAsync;

            _logger.LogWarning(
                "JobExecutionResultConsumer started (session-disabled, limited ordering guarantee) → {Queue}",
                _options.Queues.ExecutionResults);
            
            await _regularProcessor.StartProcessingAsync(ct);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_sessionProcessor is not null)
            await _sessionProcessor.StopProcessingAsync(ct);
        if (_regularProcessor is not null)
            await _regularProcessor.StopProcessingAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Session detection
    // -----------------------------------------------------------------------

    private async Task<bool> DetectSessionSupportAsync(CancellationToken ct)
    {
        try
        {
            // Try to create a temporary queue with RequiresSession to detect tier
            var adminClient = new Azure.Messaging.ServiceBus.Administration.ServiceBusAdministrationClient(
                _options.ConnectionString);
            var testQueueName = $"__test-{Guid.NewGuid():N}";
            var testOptions = new Azure.Messaging.ServiceBus.Administration.CreateQueueOptions(testQueueName) 
            { 
                RequiresSession = true 
            };
            
            try
            {
                await adminClient.CreateQueueAsync(testOptions, ct);
                // If successful, clean up and return true
                await adminClient.DeleteQueueAsync(testQueueName, ct);
                return true;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 400 && ex.Message.Contains("Basic"))
            {
                // Basic tier doesn't support sessions
                return false;
            }
        }
        catch
        {
            // If we can't detect, assume no sessions (safer for Basic tier)
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Message handlers
    // -----------------------------------------------------------------------

    private async Task ProcessSessionMessageAsync(ProcessSessionMessageEventArgs args)
    {
        await ProcessExecutionResultAsync(args.Message, args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private async Task ProcessRegularMessageAsync(ProcessMessageEventArgs args)
    {
        await ProcessExecutionResultAsync(args.Message, args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private async Task ProcessExecutionResultAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        var msg = message.Body.ToObjectFromJson<JobExecutionResultMessage>()!;

        _logger.LogInformation(
            "Processing execution result: JobId={JobId} ScheduleId={ScheduleId} Status={Status}",
            msg.Payload.JobId, msg.Payload.ScheduleId, msg.Payload.Status);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var readRepo = scope.ServiceProvider.GetRequiredService<IJobReadRepository>();
            var writeRepo = scope.ServiceProvider.GetRequiredService<IJobWriteRepository>();

            // 1. Update the current schedule status (Succeeded / Failed)
            try
            {
                await writeRepo.UpdateScheduleStatusAsync(
                    msg.Payload.ScheduleId, msg.Payload.Status, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Schedule was cascade-deleted when the job was deleted — safe to continue.
                _logger.LogWarning("Schedule {ScheduleId} not found — job may have been deleted.", msg.Payload.ScheduleId);
            }

            // 2. Record the execution audit entry (Strong write to primary)
            await writeRepo.CreateExecutionAsync(new JobExecution
            {
                JobId = msg.Payload.JobId,
                StartedAt = msg.Payload.StartedAt,
                FinishedAt = msg.Payload.CompletedAt,
                Result = msg.Payload.Status
            }, cancellationToken);

            _logger.LogInformation(
                "Recorded JobExecution: JobId={JobId} Result={Result} Duration={Duration}ms",
                msg.Payload.JobId, msg.Payload.Status, msg.Payload.DurationMs);

            // 3. Schedule the next run (only if the job still exists)
            var job = await readRepo.GetByIdAsync(
                msg.Payload.JobId, ConsistencyLevel.Strong, cancellationToken);

            string jobName = "Unknown";

            if (job is not null)
            {
                jobName = job.Name;
                var nextRunTime = SchedulingHelper.ComputeNextRunTime(job.Frequency, job.ExecutionTime);

                await writeRepo.CreateScheduleAsync(new JobSchedule
                {
                    JobId = job.Id,
                    NextRunTime = nextRunTime,
                    Status = "Pending"
                }, cancellationToken);

                _logger.LogInformation(
                    "Scheduled next run: JobId={JobId} NextRunTime={NextRunTime} ({Frequency})",
                    job.Id, nextRunTime, job.Frequency);
            }
            else
            {
                _logger.LogWarning(
                    "Job {JobId} no longer exists — skipping next schedule creation.", msg.Payload.JobId);
            }

            // 4. Send user notification
            await _publisher.SendNotificationAsync(new JobExecutionNotificationMessage(
                MessageId: $"notif-{Guid.NewGuid()}",
                Payload: new JobExecutionNotificationPayload(
                    JobId: msg.Payload.JobId,
                    JobName: jobName,
                    EventType: "ExecutionCompleted",
                    Status: msg.Payload.Status,
                    StartedAt: msg.Payload.StartedAt,
                    CompletedAt: msg.Payload.CompletedAt,
                    ErrorMessage: msg.Payload.ErrorMessage)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Error processing execution result for JobId={JobId} — abandoning for retry.",
                msg.Payload.JobId);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "JobExecutionResultConsumer error: Source={Source} EntityPath={EntityPath}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionProcessor is not null)
            await _sessionProcessor.DisposeAsync();
        if (_regularProcessor is not null)
            await _regularProcessor.DisposeAsync();
    }
}

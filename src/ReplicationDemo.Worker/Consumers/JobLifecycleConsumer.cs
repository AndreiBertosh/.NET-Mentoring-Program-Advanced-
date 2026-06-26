using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Domain.Entities;
using ReplicationDemo.Domain.Repositories;
using ReplicationDemo.Messaging;
using ReplicationDemo.Messaging.Messages;
using ReplicationDemo.Worker.Services;

namespace ReplicationDemo.Worker.Consumers;

/// <summary>
/// Consumes messages from the <c>job-lifecycle</c> topic / <c>orchestrator-sync</c> subscription.
///
/// <list type="bullet">
///   <item><b>job.created</b> — computes <c>NextRunTime</c> and inserts an initial <c>Pending</c>
///   <see cref="JobSchedule"/> on the primary, unless one already exists (idempotency).</item>
///   <item><b>job.updated</b> — deletes existing <c>Pending</c> schedules and creates a new one
///   based on the updated frequency/executionTime.</item>
///   <item><b>job.deleted</b> — logs and acknowledges; the DB cascade already removed schedules.</item>
/// </list>
///
/// Error handling:
/// <list type="bullet">
///   <item>Transient errors → abandon message (Service Bus retries up to MaxDeliveryCount).</item>
///   <item>Permanent/business errors → dead-letter explicitly.</item>
/// </list>
/// </summary>
public sealed class JobLifecycleConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobLifecycleConsumer> _logger;

    private ServiceBusProcessor? _processor;
    private bool _topicsSupported = false;

    public JobLifecycleConsumer(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<JobLifecycleConsumer> logger)
    {
        _client = client;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _topicsSupported = await DetectTopicSupportAsync(ct);

        if (_topicsSupported)
        {
            _processor = _client.CreateProcessor(
                _options.Topics.JobLifecycle,
                _options.Subscriptions.OrchestratorSync,
                new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = 4
                });
            _logger.LogInformation("JobLifecycleConsumer started \u2192 topic {Topic}/{Sub}",
                _options.Topics.JobLifecycle, _options.Subscriptions.OrchestratorSync);
        }
        else
        {
            // Basic tier fallback: consume from the job-lifecycle queue.
            _processor = _client.CreateProcessor(
                _options.Queues.JobLifecycle,
                new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = 4
                });
            _logger.LogWarning(
                "JobLifecycleConsumer: topic unavailable (Basic tier) \u2014 consuming from queue '{Queue}'.",
                _options.Queues.JobLifecycle);
        }

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;
        await _processor.StartProcessingAsync(ct);
    }

    private async Task<bool> DetectTopicSupportAsync(CancellationToken ct)
    {
        try
        {
            var adminClient = new ServiceBusAdministrationClient(_options.ConnectionString);
            // If the provisioner already ran, the topic exists on Standard tier.
            return await adminClient.TopicExistsAsync(_options.Topics.JobLifecycle, ct);
        }
        catch (Exception ex) when (
            ex.Message.Contains("Basic") ||
            (ex.InnerException?.Message.Contains("Basic") == true))
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check topic availability; using queue fallback.");
            return false;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_processor is not null)
            await _processor.StopProcessingAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Message handler
    // -----------------------------------------------------------------------

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var subject = args.Message.Subject;

        _logger.LogInformation("Received {Subject} | MessageId={MessageId}", subject, args.Message.MessageId);

        using var scope = _scopeFactory.CreateScope();
        var writeRepo = scope.ServiceProvider.GetRequiredService<IJobWriteRepository>();
        var readRepo = scope.ServiceProvider.GetRequiredService<IJobReadRepository>();

        try
        {
            switch (subject)
            {
                case "job.created":
                    var created = args.Message.Body.ToObjectFromJson<JobCreatedEvent>()!;
                    await HandleJobCreatedAsync(created, readRepo, writeRepo, args.CancellationToken);
                    break;

                case "job.updated":
                    var updated = args.Message.Body.ToObjectFromJson<JobUpdatedEvent>()!;
                    await HandleJobUpdatedAsync(updated, writeRepo, args.CancellationToken);
                    break;

                case "job.deleted":
                    var deleted = args.Message.Body.ToObjectFromJson<JobDeletedEvent>()!;
                    _logger.LogInformation(
                        "Job deleted (cascade already removed DB schedules): JobId={JobId}", deleted.Payload.JobId);
                    break;

                default:
                    _logger.LogWarning("Unknown message subject '{Subject}' — dead-lettering.", subject);
                    await args.DeadLetterMessageAsync(args.Message,
                        deadLetterReason: "UnknownSubject",
                        deadLetterErrorDescription: $"No handler for subject '{subject}'",
                        cancellationToken: args.CancellationToken);
                    return;
            }

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Transient error processing {Subject} — abandoning for retry.", subject);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    // -----------------------------------------------------------------------
    // Handlers
    // -----------------------------------------------------------------------

    private async Task HandleJobCreatedAsync(
        JobCreatedEvent evt,
        IJobReadRepository readRepo,
        IJobWriteRepository writeRepo,
        CancellationToken ct)
    {
        // Idempotency: skip if a Pending schedule already exists (at-least-once redelivery guard).
        if (await readRepo.HasPendingScheduleAsync(evt.Payload.JobId, ct))
        {
            _logger.LogWarning("Duplicate job.created for JobId={JobId} — schedule already exists, skipping.",
                evt.Payload.JobId);
            return;
        }

        var nextRunTime = SchedulingHelper.ComputeNextRunTime(evt.Payload.Frequency, evt.Payload.ExecutionTime);

        await writeRepo.CreateScheduleAsync(new JobSchedule
        {
            JobId = evt.Payload.JobId,
            NextRunTime = nextRunTime,
            Status = "Pending"
        }, ct);

        _logger.LogInformation(
            "Created initial schedule for JobId={JobId} NextRunTime={NextRunTime} ({Frequency})",
            evt.Payload.JobId, nextRunTime, evt.Payload.Frequency);
    }

    private async Task HandleJobUpdatedAsync(
        JobUpdatedEvent evt,
        IJobWriteRepository writeRepo,
        CancellationToken ct)
    {
        // Remove existing Pending schedules; the job's frequency/time may have changed.
        await writeRepo.DeletePendingSchedulesByJobIdAsync(evt.Payload.JobId, ct);

        var nextRunTime = SchedulingHelper.ComputeNextRunTime(evt.Payload.Frequency, evt.Payload.ExecutionTime);

        await writeRepo.CreateScheduleAsync(new JobSchedule
        {
            JobId = evt.Payload.JobId,
            NextRunTime = nextRunTime,
            Status = "Pending"
        }, ct);

        _logger.LogInformation(
            "Rescheduled JobId={JobId} NextRunTime={NextRunTime} ({Frequency})",
            evt.Payload.JobId, nextRunTime, evt.Payload.Frequency);
    }

    // -----------------------------------------------------------------------
    // Error handler
    // -----------------------------------------------------------------------

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "JobLifecycleConsumer error: Source={Source} EntityPath={EntityPath}",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
            await _processor.DisposeAsync();
    }
}

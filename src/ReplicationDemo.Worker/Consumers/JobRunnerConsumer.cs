using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Messaging;
using ReplicationDemo.Messaging.Messages;
using ReplicationDemo.Messaging.Publishing;
using ReplicationDemo.Worker.Services;

namespace ReplicationDemo.Worker.Consumers;

/// <summary>
/// UC2.1b — Job Runner consumer.
/// Attempts to use session-enabled processor if the namespace supports it; falls back to
/// regular processor for Basic tier (no session support).
/// 
/// Sessions ensure per-job ordering without concurrent runs; without sessions, messages
/// are processed in parallel across multiple instances (weaker ordering guarantee).
/// </summary>
public sealed class JobRunnerConsumer : IHostedService, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly IMessagePublisher _publisher;
    private readonly IJobRunnerMetrics _metrics;
    private readonly ILogger<JobRunnerConsumer> _logger;

    private ServiceBusSessionProcessor? _sessionProcessor;
    private ServiceBusProcessor? _regularProcessor;
    private bool _useSessions = false;

    public JobRunnerConsumer(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        IMessagePublisher publisher,
        IJobRunnerMetrics metrics,
        ILogger<JobRunnerConsumer> logger)
    {
        _client = client;
        _options = options.Value;
        _publisher = publisher;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Try to detect if the namespace supports sessions
        _useSessions = await DetectSessionSupportAsync(ct);

        if (_useSessions)
        {
            _sessionProcessor = _client.CreateSessionProcessor(
                _options.Queues.ExecutionRequests,
                new ServiceBusSessionProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentSessions = 4,
                    MaxConcurrentCallsPerSession = 1
                });

            _sessionProcessor.ProcessMessageAsync += ProcessSessionMessageAsync;
            _sessionProcessor.ProcessErrorAsync += ProcessErrorAsync;

            _logger.LogInformation("JobRunnerConsumer started (session-enabled) → {Queue}",
                _options.Queues.ExecutionRequests);
            
            await _sessionProcessor.StartProcessingAsync(ct);
        }
        else
        {
            _regularProcessor = _client.CreateProcessor(
                _options.Queues.ExecutionRequests,
                new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = 4
                });

            _regularProcessor.ProcessMessageAsync += ProcessRegularMessageAsync;
            _regularProcessor.ProcessErrorAsync += ProcessErrorAsync;

            _logger.LogWarning(
                "JobRunnerConsumer started (session-disabled, limited ordering guarantee) → {Queue}",
                _options.Queues.ExecutionRequests);
            
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
        await ProcessExecutionRequestAsync(args.Message, args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private async Task ProcessRegularMessageAsync(ProcessMessageEventArgs args)
    {
        await ProcessExecutionRequestAsync(args.Message, args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    private async Task ProcessExecutionRequestAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        var msg = message.Body.ToObjectFromJson<JobExecutionRequestMessage>()!;
        var startedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Runner executing JobId={JobId} ScheduleId={ScheduleId} ApiEndpoint={ApiEndpoint}",
            msg.Payload.JobId, msg.Payload.ScheduleId, msg.Payload.ApiEndpoint);

        try
        {
            var (status, httpCode, error) = await SimulateExecutionAsync(
                msg.Payload.ApiEndpoint, cancellationToken);

            var completedAt = DateTime.UtcNow;

            var result = new JobExecutionResultMessage(
                MessageId: $"exec-result-{msg.Payload.JobId}-{msg.Payload.ScheduleId}",
                SessionId: msg.Payload.JobId.ToString(),
                Payload: new JobExecutionResultPayload(
                    JobId: msg.Payload.JobId,
                    ScheduleId: msg.Payload.ScheduleId,
                    Status: status,
                    StartedAt: startedAt,
                    CompletedAt: completedAt,
                    DurationMs: (int)(completedAt - startedAt).TotalMilliseconds,
                    HttpStatusCode: httpCode,
                    ErrorMessage: error,
                    RetryCount: message.DeliveryCount - 1));

            await _publisher.SendExecutionResultAsync(result, cancellationToken);

            // Telemetry: record outcome + duration (non-blocking, never throws).
            _metrics.RecordExecution(status, result.Payload.DurationMs);

            _logger.LogInformation(
                "Runner finished JobId={JobId} ScheduleId={ScheduleId} Status={Status} Duration={Duration}ms",
                msg.Payload.JobId, msg.Payload.ScheduleId, status, result.Payload.DurationMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Count the unhandled failure so it surfaces in FailedTasksCount / alerts.
            _metrics.RecordExecution("Failed", (DateTime.UtcNow - startedAt).TotalMilliseconds);

            _logger.LogError(ex,
                "Runner failed JobId={JobId} ScheduleId={ScheduleId} — abandoning for retry.",
                msg.Payload.JobId, msg.Payload.ScheduleId);
        }
    }

    // -----------------------------------------------------------------------
    // Execution simulation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Simulates calling an external API endpoint.
    /// In production this would make a real HTTP request to <paramref name="apiEndpoint"/>.
    /// Returns (Status, HttpStatusCode?, ErrorMessage?).
    /// </summary>
    private static async Task<(string Status, int? HttpCode, string? Error)> SimulateExecutionAsync(
        string apiEndpoint,
        CancellationToken ct)
    {
        // Simulate variable execution time (0.5 – 3 s)
        await Task.Delay(Random.Shared.Next(500, 3000), ct);

        // 80% success rate
        if (Random.Shared.NextDouble() < 0.80)
            return ("Succeeded", 200, null);

        return ("Failed", null, $"Simulated failure calling {apiEndpoint}");
    }

    // -----------------------------------------------------------------------
    // Error handler
    // -----------------------------------------------------------------------

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "JobRunnerConsumer error: Source={Source} EntityPath={EntityPath}",
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

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Messaging.Messages;

namespace ReplicationDemo.Messaging.Publishing;

/// <summary>
/// Azure Service Bus implementation of <see cref="IMessagePublisher"/>.
/// Senders are created once and reused — <see cref="ServiceBusSender"/> is thread-safe.
/// </summary>
public sealed class ServiceBusMessagePublisher : IMessagePublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<ServiceBusMessagePublisher> _logger;

    private readonly ServiceBusSender _jobLifecycleSender;      // topic (Standard/Premium)
    private readonly ServiceBusSender _jobLifecycleQueueSender; // queue (Basic-tier fallback)
    private readonly ServiceBusSender _executionRequestSender;
    private readonly ServiceBusSender _executionResultSender;
    private readonly ServiceBusSender _notificationSender;

    // Set to true after the first MessagingEntityNotFound on the topic so subsequent
    // publishes go directly to the queue without an extra round-trip attempt.
    private volatile bool _topicsUnavailable = false;

    public ServiceBusMessagePublisher(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        ILogger<ServiceBusMessagePublisher> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;

        _jobLifecycleSender      = _client.CreateSender(_options.Topics.JobLifecycle);
        _jobLifecycleQueueSender = _client.CreateSender(_options.Queues.JobLifecycle);
        _executionRequestSender  = _client.CreateSender(_options.Queues.ExecutionRequests);
        _executionResultSender   = _client.CreateSender(_options.Queues.ExecutionResults);
        _notificationSender      = _client.CreateSender(_options.Queues.Notifications);
    }

    public async Task PublishJobCreatedAsync(JobCreatedEvent evt, CancellationToken ct = default)
    {
        var msg = new ServiceBusMessage(BinaryData.FromObjectAsJson(evt))
        {
            Subject = "job.created",
            MessageId = $"job.created.{evt.Payload.JobId}",
            CorrelationId = evt.CorrelationId
        };

        await SendJobLifecycleAsync(msg, ct);
        _logger.LogInformation("[SB] Published job.created | JobId={JobId}", evt.Payload.JobId);
    }

    public async Task PublishJobUpdatedAsync(JobUpdatedEvent evt, CancellationToken ct = default)
    {
        var msg = new ServiceBusMessage(BinaryData.FromObjectAsJson(evt))
        {
            Subject = "job.updated",
            MessageId = $"job.updated.{evt.Payload.JobId}.{evt.Payload.UpdatedAt.Ticks}",
            CorrelationId = evt.CorrelationId
        };

        await SendJobLifecycleAsync(msg, ct);
        _logger.LogInformation("[SB] Published job.updated | JobId={JobId}", evt.Payload.JobId);
    }

    public async Task PublishJobDeletedAsync(JobDeletedEvent evt, CancellationToken ct = default)
    {
        var msg = new ServiceBusMessage(BinaryData.FromObjectAsJson(evt))
        {
            Subject = "job.deleted",
            MessageId = $"job.deleted.{evt.Payload.JobId}",
            CorrelationId = evt.CorrelationId
        };

        await SendJobLifecycleAsync(msg, ct);
        _logger.LogInformation("[SB] Published job.deleted | JobId={JobId}", evt.Payload.JobId);
    }

    /// <summary>
    /// Sends a lifecycle message to the topic on Standard/Premium tier, or falls back to the
    /// <c>job-lifecycle</c> queue on Basic tier (after the first <c>MessagingEntityNotFound</c>).
    /// </summary>
    private async Task SendJobLifecycleAsync(ServiceBusMessage msg, CancellationToken ct)
    {
        if (_topicsUnavailable)
        {
            await _jobLifecycleQueueSender.SendMessageAsync(msg, ct);
            return;
        }

        try
        {
            await _jobLifecycleSender.SendMessageAsync(msg, ct);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            _topicsUnavailable = true;
            _logger.LogWarning(
                "[SB] Topic '{Topic}' not found — switching to queue fallback '{Queue}' for all lifecycle events.",
                _options.Topics.JobLifecycle, _options.Queues.JobLifecycle);
            await _jobLifecycleQueueSender.SendMessageAsync(msg, ct);
        }
    }

    public async Task SendExecutionRequestAsync(JobExecutionRequestMessage msg, CancellationToken ct = default)
    {
        var sbMsg = new ServiceBusMessage(BinaryData.FromObjectAsJson(msg))
        {
            MessageId = msg.MessageId,
            SessionId = msg.SessionId,
            Subject = "job.execution.request"
        };

        await _executionRequestSender.SendMessageAsync(sbMsg, ct);
        _logger.LogInformation("[SB] Sent execution request → {Queue} | JobId={JobId} ScheduleId={ScheduleId}",
            _options.Queues.ExecutionRequests, msg.Payload.JobId, msg.Payload.ScheduleId);
    }

    public async Task SendExecutionResultAsync(JobExecutionResultMessage msg, CancellationToken ct = default)
    {
        var sbMsg = new ServiceBusMessage(BinaryData.FromObjectAsJson(msg))
        {
            MessageId = msg.MessageId,
            SessionId = msg.SessionId,
            Subject = "job.execution.result"
        };

        await _executionResultSender.SendMessageAsync(sbMsg, ct);
        _logger.LogInformation("[SB] Sent execution result → {Queue} | JobId={JobId} Status={Status}",
            _options.Queues.ExecutionResults, msg.Payload.JobId, msg.Payload.Status);
    }

    public async Task SendNotificationAsync(JobExecutionNotificationMessage msg, CancellationToken ct = default)
    {
        var sbMsg = new ServiceBusMessage(BinaryData.FromObjectAsJson(msg))
        {
            MessageId = msg.MessageId,
            Subject = "job.execution.notification"
        };

        await _notificationSender.SendMessageAsync(sbMsg, ct);
        _logger.LogInformation("[SB] Sent notification → {Queue} | JobId={JobId} EventType={EventType}",
            _options.Queues.Notifications, msg.Payload.JobId, msg.Payload.EventType);
    }

    public async ValueTask DisposeAsync()
    {
        await _jobLifecycleSender.DisposeAsync();
        await _jobLifecycleQueueSender.DisposeAsync();
        await _executionRequestSender.DisposeAsync();
        await _executionResultSender.DisposeAsync();
        await _notificationSender.DisposeAsync();
    }
}

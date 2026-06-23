using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Messaging;

namespace ReplicationDemo.Worker.Services;

/// <summary>
/// Dead Letter Queue (DLQ) monitor — Optional pattern as required by the task.
/// Runs every 5 minutes, peeks at the DLQ of every queue and subscription, and logs
/// dead-lettered messages. For the <c>job-execution-requests</c> DLQ this provides
/// visibility into jobs that the runner failed to process after all retries.
/// Stuck Running schedules are recovered automatically by <see cref="SchedulePollingService"/>'s
/// reconciliation pass.
/// </summary>
public sealed class DlqMonitorService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PeekTimeout = TimeSpan.FromSeconds(3);
    private const int PeekBatch = 20;

    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<DlqMonitorService> _logger;

    public DlqMonitorService(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        ILogger<DlqMonitorService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DlqMonitorService started. Checking every {Interval}min.", CheckInterval.TotalMinutes);

        using var timer = new PeriodicTimer(CheckInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckQueueDlqAsync(_options.Queues.ExecutionRequests, stoppingToken);
            await CheckQueueDlqAsync(_options.Queues.ExecutionResults, stoppingToken);
            await CheckQueueDlqAsync(_options.Queues.Notifications, stoppingToken);
            await CheckTopicDlqAsync(
                _options.Topics.JobLifecycle,
                _options.Subscriptions.OrchestratorSync,
                stoppingToken);
            await CheckTopicDlqAsync(
                _options.Topics.JobLifecycle,
                _options.Subscriptions.AuditLog,
                stoppingToken);
        }
    }

    private async Task CheckQueueDlqAsync(string queueName, CancellationToken ct)
    {
        await using var receiver = _client.CreateReceiver(queueName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        });

        try
        {
            var messages = await receiver.PeekMessagesAsync(PeekBatch, cancellationToken: ct);

            foreach (var msg in messages)
            {
                _logger.LogError(
                    "[DLQ] Queue={Queue} MessageId={MessageId} Subject={Subject} " +
                    "DeadLetterReason={Reason} DeliveryCount={Count} EnqueuedAt={EnqueuedAt}",
                    queueName, msg.MessageId, msg.Subject,
                    msg.DeadLetterReason, msg.DeliveryCount, msg.EnqueuedTime);
            }

            if (messages.Count > 0)
                _logger.LogWarning("[DLQ] Queue={Queue} has {Count} dead-lettered message(s).", queueName, messages.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[DLQ] Error peeking DLQ for queue {Queue}.", queueName);
        }
    }

    private async Task CheckTopicDlqAsync(string topicName, string subscriptionName, CancellationToken ct)
    {
        await using var receiver = _client.CreateReceiver(topicName, subscriptionName, new ServiceBusReceiverOptions
        {
            SubQueue = SubQueue.DeadLetter,
            ReceiveMode = ServiceBusReceiveMode.PeekLock
        });

        try
        {
            var messages = await receiver.PeekMessagesAsync(PeekBatch, cancellationToken: ct);

            foreach (var msg in messages)
            {
                _logger.LogError(
                    "[DLQ] Topic={Topic} Subscription={Sub} MessageId={MessageId} Subject={Subject} " +
                    "DeadLetterReason={Reason} DeliveryCount={Count} EnqueuedAt={EnqueuedAt}",
                    topicName, subscriptionName, msg.MessageId, msg.Subject,
                    msg.DeadLetterReason, msg.DeliveryCount, msg.EnqueuedTime);
            }

            if (messages.Count > 0)
                _logger.LogWarning("[DLQ] Topic={Topic}/{Sub} has {Count} dead-lettered message(s).",
                    topicName, subscriptionName, messages.Count);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            _logger.LogWarning(
                "[DLQ] Topic subscription {Topic}/{Sub} not found — entity may not be provisioned or tier does not support topics. Skipping.",
                topicName, subscriptionName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[DLQ] Error peeking DLQ for topic {Topic}/{Sub}.", topicName, subscriptionName);
        }
    }
}

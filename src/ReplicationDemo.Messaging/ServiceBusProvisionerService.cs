using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReplicationDemo.Messaging;

/// <summary>
/// Runs once at startup and ensures all required Azure Service Bus entities exist.
/// Registered via <see cref="DependencyInjection.AddMessaging"/> so both the API and Worker
/// provision entities before any publisher or consumer is used.
/// <list type="bullet">
///   <item>The <c>job-lifecycle</c> queue is ALWAYS provisioned — it is the Basic-tier fallback
///   when the topic is unavailable.</item>
///   <item>The <c>job-lifecycle</c> topic + subscriptions are provisioned only on Standard/Premium.</item>
/// </list>
/// </summary>
public sealed class ServiceBusProvisionerService : IHostedService
{
    private readonly ServiceBusAdministrationClient _adminClient;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<ServiceBusProvisionerService> _logger;

    public ServiceBusProvisionerService(
        IOptions<ServiceBusOptions> options,
        ILogger<ServiceBusProvisionerService> logger)
    {
        _options = options.Value;
        _adminClient = new ServiceBusAdministrationClient(_options.ConnectionString);
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Provisioning Azure Service Bus entities…");

        var supportsTopics = await DetectTopicSupportAsync(ct);

        // ------------------------------------------------------------------
        // Queues — always provisioned (works on every tier)
        // ------------------------------------------------------------------
        await EnsureQueueAsync(_options.Queues.ExecutionRequests,
            requiresSession: true, lockDuration: TimeSpan.FromMinutes(5), maxDeliveryCount: 5, ct);

        await EnsureQueueAsync(_options.Queues.ExecutionResults,
            requiresSession: true, lockDuration: TimeSpan.FromMinutes(1), maxDeliveryCount: 5, ct);

        await EnsureQueueAsync(_options.Queues.Notifications,
            requiresSession: false, lockDuration: TimeSpan.FromSeconds(30), maxDeliveryCount: 3, ct);

        // job-lifecycle queue: always provisioned as Basic-tier fallback
        await EnsureQueueAsync(_options.Queues.JobLifecycle,
            requiresSession: false, lockDuration: TimeSpan.FromMinutes(1), maxDeliveryCount: 10, ct);

        // ------------------------------------------------------------------
        // Topic + subscriptions — Standard/Premium only
        // ------------------------------------------------------------------
        if (supportsTopics)
        {
            await EnsureTopicAsync(_options.Topics.JobLifecycle, ct);
            await EnsureSubscriptionAsync(_options.Topics.JobLifecycle,
                _options.Subscriptions.OrchestratorSync,
                lockDuration: TimeSpan.FromMinutes(1), maxDeliveryCount: 10, ct);
            await EnsureSubscriptionAsync(_options.Topics.JobLifecycle,
                _options.Subscriptions.AuditLog,
                lockDuration: TimeSpan.FromSeconds(30), maxDeliveryCount: 5, ct);
        }
        else
        {
            _logger.LogWarning(
                "Basic tier namespace detected — skipping topic provisioning. " +
                "Lifecycle events will use the '{Queue}' queue fallback.",
                _options.Queues.JobLifecycle);
        }

        _logger.LogInformation("Service Bus provisioning complete.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // -----------------------------------------------------------------------
    // Tier detection
    // -----------------------------------------------------------------------

    private async Task<bool> DetectTopicSupportAsync(CancellationToken ct)
    {
        try
        {
            var testTopicName = $"__probe-{Guid.NewGuid():N}";
            await _adminClient.CreateTopicAsync(new CreateTopicOptions(testTopicName), ct);
            await _adminClient.DeleteTopicAsync(testTopicName, ct);
            _logger.LogDebug("Service Bus namespace supports topics (Standard/Premium tier).");
            return true;
        }
        catch (Exception ex) when (
            ex.Message.Contains("Basic") ||
            (ex.InnerException?.Message.Contains("Basic") == true))
        {
            _logger.LogDebug("Service Bus namespace is Basic tier — topics not supported.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not verify namespace tier (check Manage permissions). Assuming Basic tier — topics skipped.");
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task EnsureQueueAsync(
        string queueName, bool requiresSession,
        TimeSpan lockDuration, int maxDeliveryCount,
        CancellationToken ct)
    {
        try
        {
            if (await _adminClient.QueueExistsAsync(queueName, ct))
            {
                _logger.LogDebug("Queue already exists: {Queue}", queueName);
                return;
            }

            await _adminClient.CreateQueueAsync(new CreateQueueOptions(queueName)
            {
                RequiresSession = requiresSession,
                LockDuration = lockDuration,
                MaxDeliveryCount = maxDeliveryCount,
                DefaultMessageTimeToLive = TimeSpan.FromHours(1),
                DeadLetteringOnMessageExpiration = true
            }, ct);

            _logger.LogInformation("Created queue: {Queue} (sessions={Sessions})", queueName, requiresSession);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogDebug("Queue {Queue} already exists (concurrent creation — ignored).", queueName);
        }
    }

    private async Task EnsureTopicAsync(string topicName, CancellationToken ct)
    {
        try
        {
            if (await _adminClient.TopicExistsAsync(topicName, ct))
            {
                _logger.LogDebug("Topic already exists: {Topic}", topicName);
                return;
            }

            await _adminClient.CreateTopicAsync(new CreateTopicOptions(topicName)
            {
                DefaultMessageTimeToLive = TimeSpan.FromHours(1)
            }, ct);

            _logger.LogInformation("Created topic: {Topic}", topicName);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogDebug("Topic {Topic} already exists (concurrent creation — ignored).", topicName);
        }
    }

    private async Task EnsureSubscriptionAsync(
        string topicName, string subscriptionName,
        TimeSpan lockDuration, int maxDeliveryCount,
        CancellationToken ct)
    {
        try
        {
            if (await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, ct))
            {
                _logger.LogDebug("Subscription already exists: {Topic}/{Sub}", topicName, subscriptionName);
                return;
            }

            await _adminClient.CreateSubscriptionAsync(new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                LockDuration = lockDuration,
                MaxDeliveryCount = maxDeliveryCount,
                DefaultMessageTimeToLive = TimeSpan.FromHours(1),
                DeadLetteringOnMessageExpiration = true
            }, ct);

            _logger.LogInformation("Created subscription: {Topic}/{Sub}", topicName, subscriptionName);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogDebug("Subscription {Topic}/{Sub} already exists (concurrent creation — ignored).",
                topicName, subscriptionName);
        }
    }
}

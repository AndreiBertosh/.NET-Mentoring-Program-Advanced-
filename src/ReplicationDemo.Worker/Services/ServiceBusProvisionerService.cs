using Azure;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReplicationDemo.Messaging;

namespace ReplicationDemo.Worker.Services;

/// <summary>
/// Runs once at Worker startup and ensures all required Azure Service Bus entities exist.
/// Uses <see cref="ServiceBusAdministrationClient"/> (same connection string as the runtime client).
/// Registered first in <c>Program.cs</c> so all queues/topics/subscriptions are in place
/// before any consumer's <c>StartAsync</c> is called.
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

        // Detect namespace tier to decide what features are available
        var tierInfo = await DetectNamespaceTierAsync(ct);
        bool supportsSession = tierInfo.supportsSession;
        bool supportsTopics = tierInfo.supportsTopics;

        if (!supportsSession)
        {
            _logger.LogWarning(
                "Azure Service Bus namespace is using Basic tier, which does not support sessions. " +
                "Session-based message ordering will not be available.");
        }

        if (!supportsTopics)
        {
            _logger.LogWarning(
                "Azure Service Bus namespace is using Basic tier, which does not support topics. " +
                "Topic-based publishing will not be available.");
        }

        // Queues
        await EnsureQueueAsync(_options.Queues.ExecutionRequests, requiresSession: supportsSession,
            lockDuration: TimeSpan.FromMinutes(5), maxDeliveryCount: 5, ct);

        await EnsureQueueAsync(_options.Queues.ExecutionResults, requiresSession: supportsSession,
            lockDuration: TimeSpan.FromMinutes(1), maxDeliveryCount: 5, ct);

        await EnsureQueueAsync(_options.Queues.Notifications, requiresSession: false,
            lockDuration: TimeSpan.FromSeconds(30), maxDeliveryCount: 3, ct);

        // Topic + subscriptions (only on Standard/Premium tier)
        if (supportsTopics)
        {
            await EnsureTopicAsync(_options.Topics.JobLifecycle, ct);
            await EnsureSubscriptionAsync(_options.Topics.JobLifecycle,
                _options.Subscriptions.OrchestratorSync, lockDuration: TimeSpan.FromMinutes(1),
                maxDeliveryCount: 10, ct);
            await EnsureSubscriptionAsync(_options.Topics.JobLifecycle,
                _options.Subscriptions.AuditLog, lockDuration: TimeSpan.FromSeconds(30),
                maxDeliveryCount: 5, ct);
        }
        else
        {
            _logger.LogInformation("Skipping topic provisioning on Basic tier namespace.");
        }

        _logger.LogInformation("Service Bus provisioning complete.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // -----------------------------------------------------------------------
    // Tier Detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects namespace tier and returns supported features.
    /// Basic: queues only, no sessions, no topics
    /// Standard/Premium: queues with sessions, topics, subscriptions
    /// </summary>
    private async Task<(bool supportsSession, bool supportsTopics)> DetectNamespaceTierAsync(CancellationToken ct)
    {
        // Try to create a test topic to detect tier
        // If fails with "Basic tier" error, topics aren't supported
        try
        {
            var testTopicName = $"__test-{Guid.NewGuid():N}";
            var testOptions = new CreateTopicOptions(testTopicName);
            
            await _adminClient.CreateTopicAsync(testOptions, ct);
            // If successful, clean up and return full support
            await _adminClient.DeleteTopicAsync(testTopicName, ct);
            _logger.LogDebug("Detected Standard/Premium tier Service Bus namespace — sessions and topics supported");
            return (supportsSession: true, supportsTopics: true);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 400 && ex.Message.Contains("Basic"))
        {
            // Basic tier doesn't support topics
            _logger.LogDebug("Detected Basic tier Service Bus namespace — queues only");
            return (supportsSession: false, supportsTopics: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect namespace tier; assuming Basic tier (no sessions, no topics).");
            return (supportsSession: false, supportsTopics: false);
        }
    }

    private async Task EnsureQueueAsync(
        string queueName,
        bool requiresSession,
        TimeSpan lockDuration,
        int maxDeliveryCount,
        CancellationToken ct)
    {
        if (await _adminClient.QueueExistsAsync(queueName, ct))
        {
            _logger.LogDebug("Queue already exists: {Queue}", queueName);
            return;
        }

        var options = new CreateQueueOptions(queueName)
        {
            RequiresSession = requiresSession,
            LockDuration = lockDuration,
            MaxDeliveryCount = maxDeliveryCount,
            DefaultMessageTimeToLive = TimeSpan.FromHours(1),
            DeadLetteringOnMessageExpiration = true
        };

        await _adminClient.CreateQueueAsync(options, ct);
        _logger.LogInformation("Created queue: {Queue} (sessions={Sessions})", queueName, requiresSession);
    }

    private async Task EnsureTopicAsync(string topicName, CancellationToken ct)
    {
        if (await _adminClient.TopicExistsAsync(topicName, ct))
        {
            _logger.LogDebug("Topic already exists: {Topic}", topicName);
            return;
        }

        var options = new CreateTopicOptions(topicName)
        {
            DefaultMessageTimeToLive = TimeSpan.FromHours(1)
        };

        await _adminClient.CreateTopicAsync(options, ct);
        _logger.LogInformation("Created topic: {Topic}", topicName);
    }

    private async Task EnsureSubscriptionAsync(
        string topicName,
        string subscriptionName,
        TimeSpan lockDuration,
        int maxDeliveryCount,
        CancellationToken ct)
    {
        if (await _adminClient.SubscriptionExistsAsync(topicName, subscriptionName, ct))
        {
            _logger.LogDebug("Subscription already exists: {Topic}/{Sub}", topicName, subscriptionName);
            return;
        }

        var options = new CreateSubscriptionOptions(topicName, subscriptionName)
        {
            LockDuration = lockDuration,
            MaxDeliveryCount = maxDeliveryCount,
            DefaultMessageTimeToLive = TimeSpan.FromHours(1),
            DeadLetteringOnMessageExpiration = true
        };

        await _adminClient.CreateSubscriptionAsync(options, ct);
        _logger.LogInformation("Created subscription: {Topic}/{Sub}", topicName, subscriptionName);
    }
}

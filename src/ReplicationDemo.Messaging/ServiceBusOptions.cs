namespace ReplicationDemo.Messaging;

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public string ConnectionString { get; set; } = string.Empty;

    public TopicsOptions Topics { get; set; } = new();
    public SubscriptionsOptions Subscriptions { get; set; } = new();
    public QueuesOptions Queues { get; set; } = new();
}

public sealed class TopicsOptions
{
    public string JobLifecycle { get; set; } = "job-lifecycle";
}

public sealed class SubscriptionsOptions
{
    public string OrchestratorSync { get; set; } = "orchestrator-sync";
    public string AuditLog { get; set; } = "audit-log";
}

public sealed class QueuesOptions
{
    public string ExecutionRequests { get; set; } = "job-execution-requests";
    public string ExecutionResults { get; set; } = "job-execution-results";
    public string Notifications { get; set; } = "notifications";
    /// <summary>
    /// Fallback queue used when the namespace is Basic tier and topics are unavailable.
    /// On Standard/Premium tier the <see cref="TopicsOptions.JobLifecycle"/> topic is preferred.
    /// </summary>
    public string JobLifecycle { get; set; } = "job-lifecycle";
}

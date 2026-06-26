using ReplicationDemo.Messaging.Messages;

namespace ReplicationDemo.Messaging.Publishing;

/// <summary>
/// Publishes job lifecycle events and execution messages to Azure Service Bus.
/// </summary>
public interface IMessagePublisher
{
    // Job lifecycle events → job-lifecycle topic
    Task PublishJobCreatedAsync(JobCreatedEvent evt, CancellationToken ct = default);
    Task PublishJobUpdatedAsync(JobUpdatedEvent evt, CancellationToken ct = default);
    Task PublishJobDeletedAsync(JobDeletedEvent evt, CancellationToken ct = default);

    // Execution messages → dedicated queues
    Task SendExecutionRequestAsync(JobExecutionRequestMessage msg, CancellationToken ct = default);
    Task SendExecutionResultAsync(JobExecutionResultMessage msg, CancellationToken ct = default);
    Task SendNotificationAsync(JobExecutionNotificationMessage msg, CancellationToken ct = default);
}

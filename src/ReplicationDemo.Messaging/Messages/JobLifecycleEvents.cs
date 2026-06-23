namespace ReplicationDemo.Messaging.Messages;

// ---------------------------------------------------------------------------
// UC1.1 — Job Created
// ---------------------------------------------------------------------------

public sealed record JobCreatedEvent(
    string EventId,
    DateTime OccurredAt,
    string CorrelationId,
    JobCreatedPayload Payload);

public sealed record JobCreatedPayload(
    Guid JobId,
    string Name,
    string Frequency,
    TimeSpan ExecutionTime,
    string ApiEndpoint,
    DateTime CreatedAt);

// ---------------------------------------------------------------------------
// UC1.2 — Job Updated
// ---------------------------------------------------------------------------

public sealed record JobUpdatedEvent(
    string EventId,
    DateTime OccurredAt,
    string CorrelationId,
    JobUpdatedPayload Payload);

public sealed record JobUpdatedPayload(
    Guid JobId,
    string Name,
    string Frequency,
    TimeSpan ExecutionTime,
    string ApiEndpoint,
    DateTime UpdatedAt);

// ---------------------------------------------------------------------------
// UC1.3 — Job Deleted
// ---------------------------------------------------------------------------

public sealed record JobDeletedEvent(
    string EventId,
    DateTime OccurredAt,
    string CorrelationId,
    JobDeletedPayload Payload);

public sealed record JobDeletedPayload(
    Guid JobId,
    DateTime DeletedAt);

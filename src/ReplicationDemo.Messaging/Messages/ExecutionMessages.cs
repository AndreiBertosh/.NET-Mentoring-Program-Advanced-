namespace ReplicationDemo.Messaging.Messages;

// ---------------------------------------------------------------------------
// UC2.1 — Execution Request  (Orchestrator → Runner)
// ---------------------------------------------------------------------------

public sealed record JobExecutionRequestMessage(
    string MessageId,
    string SessionId,
    JobExecutionRequestPayload Payload);

public sealed record JobExecutionRequestPayload(
    Guid JobId,
    Guid ScheduleId,
    string JobName,
    string ApiEndpoint,
    DateTime ScheduledAt,
    DateTime TriggeredAt);

// ---------------------------------------------------------------------------
// UC2.1 — Execution Result  (Runner → Orchestrator)
// ---------------------------------------------------------------------------

public sealed record JobExecutionResultMessage(
    string MessageId,
    string SessionId,
    JobExecutionResultPayload Payload);

public sealed record JobExecutionResultPayload(
    Guid JobId,
    Guid ScheduleId,
    string Status,           // "Succeeded" | "Failed"
    DateTime StartedAt,
    DateTime CompletedAt,
    int DurationMs,
    int? HttpStatusCode,
    string? ErrorMessage,
    int RetryCount);

// ---------------------------------------------------------------------------
// UC2.1 — Execution Notification  (Orchestrator → Notification Handler)
// ---------------------------------------------------------------------------

public sealed record JobExecutionNotificationMessage(
    string MessageId,
    JobExecutionNotificationPayload Payload);

public sealed record JobExecutionNotificationPayload(
    Guid JobId,
    string JobName,
    string EventType,        // "ExecutionCompleted" | "ExecutionLockFailed"
    string Status,
    DateTime StartedAt,
    DateTime CompletedAt,
    string? ErrorMessage);

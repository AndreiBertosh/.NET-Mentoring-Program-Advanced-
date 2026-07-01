using ProtoBuf;

namespace ReplicationDemo.Api.Models;

/// <summary>
/// Lightweight DTO for a single job execution record.
/// Annotated with protobuf-net attributes so the same class can be serialised
/// as JSON (baseline) or binary Protobuf — the format is chosen at runtime via
/// the HTTP <c>Accept</c> header.
/// </summary>
[ProtoContract]
public sealed class JobExecutionDto
{
    [ProtoMember(1)] public Guid Id { get; init; }
    [ProtoMember(2)] public Guid JobId { get; init; }
    [ProtoMember(3)] public DateTime StartedAt { get; init; }
    [ProtoMember(4)] public DateTime? FinishedAt { get; init; }
    [ProtoMember(5)] public string Result { get; init; } = string.Empty;
}

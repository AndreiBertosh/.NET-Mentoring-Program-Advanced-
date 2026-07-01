using ProtoBuf;

namespace ReplicationDemo.Api.Models;

/// <summary>
/// Paginated response for UC 2.3 — View Job Execution History.
/// Carries a page of <see cref="JobExecutionDto"/> records together with
/// pagination metadata. Annotated for both JSON and Protobuf serialisation.
/// </summary>
[ProtoContract]
public sealed class ExecutionHistoryResponse
{
    [ProtoMember(1)] public Guid JobId { get; init; }
    [ProtoMember(2)] public int TotalCount { get; init; }
    [ProtoMember(3)] public int Page { get; init; }
    [ProtoMember(4)] public int PageSize { get; init; }
    [ProtoMember(5)] public List<JobExecutionDto> Executions { get; init; } = [];
}

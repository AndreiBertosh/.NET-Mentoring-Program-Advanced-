namespace ReplicationDemo.Api.Models;

/// <summary>
/// Result of the in-process JSON vs Protobuf benchmark.
/// Returned by <c>GET /api/orchestrator/jobs/{jobId}/executions/benchmark</c>.
/// </summary>
public sealed class BenchmarkResult
{
    public int RecordCount { get; init; }
    public int Iterations { get; init; }
    public FormatStats Json { get; init; } = new();
    public FormatStats Protobuf { get; init; } = new();
    public string SizeReductionPercent { get; init; } = string.Empty;
    public string Recommendation { get; init; } = string.Empty;

    public sealed class FormatStats
    {
        public long PayloadBytes { get; init; }
        public string PayloadKb { get; init; } = string.Empty;
        public double AvgSerializeMs { get; init; }
        public double AvgDeserializeMs { get; init; }
    }
}

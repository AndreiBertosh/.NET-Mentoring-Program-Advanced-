namespace ReplicationDemo.Application.Consistency;

/// <summary>
/// Configuration options for consistency behaviour, bound from <c>appsettings.json</c>
/// under the key <c>"Consistency"</c>.
/// </summary>
public sealed class ConsistencySettings
{
    public const string SectionName = "Consistency";

    /// <summary>
    /// Number of seconds after a write during which subsequent reads by the same user
    /// are routed to the primary (ReadAfterWrite cooldown).
    /// Default: 5 seconds (exceeds typical sub-second transactional replication lag with margin).
    /// </summary>
    public int CooldownSeconds { get; set; } = 5;
}

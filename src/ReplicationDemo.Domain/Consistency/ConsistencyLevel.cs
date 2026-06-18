namespace ReplicationDemo.Domain.Consistency;

/// <summary>
/// Defines the consistency model to apply when reading data from a replicated database.
/// The appropriate level is determined by business requirements and encapsulated in the
/// application service layer — API consumers should not be aware of this detail.
/// </summary>
public enum ConsistencyLevel
{
    /// <summary>
    /// Always read from the primary (leader) server.
    /// Guarantees zero replication lag — every read reflects the latest committed write.
    /// <para>
    /// SQL Server routing: Primary (port 1435).
    /// Use when data correctness and freshness are business-critical and stale reads
    /// would cause incorrect behaviour (e.g., execution audit writes).
    /// </para>
    /// </summary>
    Strong,

    /// <summary>
    /// Read from the replica (subscriber) server.
    /// May observe a bounded replication lag (typically sub-second with SQL Server
    /// Transactional Replication under normal load).
    /// <para>
    /// SQL Server routing: Replica (port 1434).
    /// Use when read throughput and availability take priority over absolute freshness
    /// (e.g., monitoring dashboards, schedule polling, catalog browsing).
    /// </para>
    /// </summary>
    Eventual,

    /// <summary>
    /// Hybrid model: routes to the primary for a configurable cooldown period after
    /// the current user's last write, then falls back to <see cref="Eventual"/>.
    /// Ensures a user always sees the results of their own writes without requiring
    /// global strong consistency for all clients.
    /// <para>
    /// Implemented via the <c>Caching + Cooldown</c> pattern in
    /// <c>ConsistencyManager</c>. The effective level is resolved to either
    /// <see cref="Strong"/> or <see cref="Eventual"/> before reaching the repository.
    /// </para>
    /// </summary>
    ReadAfterWrite
}

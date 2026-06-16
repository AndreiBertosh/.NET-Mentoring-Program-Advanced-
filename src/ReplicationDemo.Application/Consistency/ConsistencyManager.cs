using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ReplicationDemo.Domain.Consistency;

namespace ReplicationDemo.Application.Consistency;

/// <summary>
/// Tracks per-user write timestamps using the <em>Caching + Cooldown</em> pattern and
/// resolves the effective <see cref="ConsistencyLevel"/> for <see cref="ConsistencyLevel.ReadAfterWrite"/>
/// reads.
///
/// <para>
/// After a user performs a write, subsequent reads by that same user are routed to the
/// primary for a configurable <see cref="ConsistencySettings.CooldownSeconds"/> window.
/// Once the window expires the read falls back to the replica (<see cref="ConsistencyLevel.Eventual"/>).
/// This ensures a user always sees their own writes without requiring global strong consistency
/// for all clients.
/// </para>
/// </summary>
public sealed class ConsistencyManager
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _cooldownPeriod;

    private const string CacheKeyPrefix = "UserLastWrite_";

    public ConsistencyManager(IMemoryCache cache, IOptions<ConsistencySettings> options)
    {
        _cache = cache;
        _cooldownPeriod = TimeSpan.FromSeconds(options.Value.CooldownSeconds);
    }

    /// <summary>
    /// Records that a write occurred for <paramref name="userId"/> and starts the cooldown window.
    /// Call this immediately after a successful write so that the next read by the same user
    /// is routed to the primary.
    /// </summary>
    public void TrackWrite(string userId)
    {
        _cache.Set(GetCacheKey(userId), DateTime.UtcNow, _cooldownPeriod);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="userId"/> is within the cooldown period
    /// after their last write, meaning reads should be routed to the primary to satisfy
    /// ReadAfterWrite guarantees.
    /// </summary>
    public bool IsReadAfterWriteApplicable(string userId)
    {
        if (_cache.TryGetValue(GetCacheKey(userId), out DateTime lastWriteTime))
        {
            return (DateTime.UtcNow - lastWriteTime) < _cooldownPeriod;
        }
        return false;
    }

    /// <summary>
    /// Resolves the effective consistency level for a read operation.
    /// <list type="bullet">
    ///   <item>If <paramref name="requested"/> is not <see cref="ConsistencyLevel.ReadAfterWrite"/>,
    ///   returns <paramref name="requested"/> unchanged.</item>
    ///   <item>If the user is within the cooldown window, upgrades to <see cref="ConsistencyLevel.Strong"/>
    ///   (primary read).</item>
    ///   <item>Otherwise downgrades to <see cref="ConsistencyLevel.Eventual"/> (replica read).</item>
    /// </list>
    /// </summary>
    public ConsistencyLevel Resolve(string userId, ConsistencyLevel requested)
    {
        if (requested != ConsistencyLevel.ReadAfterWrite)
            return requested;

        return IsReadAfterWriteApplicable(userId)
            ? ConsistencyLevel.Strong
            : ConsistencyLevel.Eventual;
    }

    private static string GetCacheKey(string userId) => $"{CacheKeyPrefix}{userId}";
}

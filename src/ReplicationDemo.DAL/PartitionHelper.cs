namespace ReplicationDemo.DAL;

/// <summary>
/// Computes SQL Server partition numbers for the <c>PF_JobExecutions_ByMonth</c>
/// partition function (RANGE RIGHT, monthly boundaries starting 2026-01-01).
///
/// Partition layout:
///   1 : StartedAt &lt;  2026-01-01         (catch-all / pre-history)
///   2 : 2026-01-01 &lt;= x &lt; 2026-02-01  (January 2026)
///   3 : 2026-02-01 &lt;= x &lt; 2026-03-01  (February 2026)
///   4 : 2026-03-01 &lt;= x &lt; 2026-04-01  (March 2026)
///   5 : 2026-04-01 &lt;= x &lt; 2026-05-01  (April 2026)
///   6 : 2026-05-01 &lt;= x &lt; 2026-06-01  (May 2026)
///   7 : 2026-06-01 &lt;= x &lt; 2026-07-01  (June 2026 — current)
///   8 : StartedAt &gt;= 2026-07-01        (future buffer)
/// </summary>
internal static class PartitionHelper
{
    /// <summary>RANGE RIGHT boundaries for PF_JobExecutions_ByMonth (UTC).</summary>
    private static readonly DateTime[] Boundaries =
    [
        new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
    ];

    /// <summary>
    /// Returns the 1-based partition number for <paramref name="startedAt"/>.
    /// Formula (RANGE RIGHT): <c>1 + count(boundaries where boundary &lt;= value)</c>.
    /// </summary>
    public static int GetPartitionNumber(DateTime startedAt)
    {
        var utc = startedAt.Kind == DateTimeKind.Utc ? startedAt : startedAt.ToUniversalTime();
        return 1 + Boundaries.Count(b => b <= utc);
    }

    /// <summary>
    /// Returns the inclusive range of partition numbers that cover
    /// [<paramref name="from"/>, <paramref name="to"/>).
    /// A <see langword="null"/> bound means "no limit on that side".
    /// </summary>
    public static (int First, int Last) GetPartitionRange(DateTime? from, DateTime? to)
    {
        var first = from.HasValue ? GetPartitionNumber(from.Value) : 1;
        // "to" is exclusive upper bound; subtract one tick so we get the partition of the last included value
        var last = to.HasValue ? GetPartitionNumber(to.Value.AddTicks(-1)) : Boundaries.Length + 1;
        return (first, last);
    }
}

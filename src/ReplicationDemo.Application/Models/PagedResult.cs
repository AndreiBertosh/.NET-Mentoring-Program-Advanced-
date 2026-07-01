namespace ReplicationDemo.Application.Models;

/// <summary>
/// A generic page of results returned from a paginated query.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);

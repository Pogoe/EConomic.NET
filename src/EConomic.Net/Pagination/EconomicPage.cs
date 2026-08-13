namespace EConomic.Pagination;

/// <summary>
/// One page of results.
/// </summary>
/// <typeparam name="T">The resource type.</typeparam>
/// <param name="Items">The items on this page.</param>
/// <param name="PageIndex">Zero-based page number, matching e-conomic's <c>skippages</c>.</param>
/// <param name="PageSize">The page size that was requested.</param>
/// <remarks>
/// Returned by the page-at-a-time API. Most callers should prefer async enumeration, which pages
/// transparently; this exists for callers that manage paging themselves, for example when
/// persisting a position between runs.
/// </remarks>
public sealed record EconomicPage<T>(IReadOnlyList<T> Items, int PageIndex, int PageSize)
{
    /// <summary>
    /// Whether another page is worth requesting.
    /// </summary>
    /// <remarks>
    /// A short page means the end of the collection. A full page might be the last one, in which
    /// case the next request returns nothing — one wasted call, which is preferable to relying on
    /// a total count that can shift under concurrent writes.
    /// </remarks>
    public bool HasMore => Items.Count >= PageSize && Items.Count > 0;
}

/// <summary>The query parameters for a single page request.</summary>
/// <param name="Filter">The <c>filter=</c> value, or <see langword="null"/> for none.</param>
/// <param name="Sort">The <c>sort=</c> value, or <see langword="null"/> for none.</param>
/// <param name="PageIndex">Zero-based page number.</param>
/// <param name="PageSize">Items per page.</param>
public readonly record struct EconomicPageRequest(string? Filter, string? Sort, int PageIndex, int PageSize);

/// <summary>Fetches one page of a collection.</summary>
/// <typeparam name="T">The resource type.</typeparam>
/// <remarks>
/// The seam between the query API and the transport. Keeping it separate means the paging and
/// query-composition logic is testable without a network or a generated client, and it is where
/// the cursor-based OpenAPI services will plug in alongside the legacy page-based API.
/// </remarks>
internal interface IEconomicPageSource<T>
{
    /// <summary>Fetches a single page.</summary>
    /// <param name="request">Which page, and how to filter and sort it.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The page.</returns>
    Task<EconomicPage<T>> GetPageAsync(EconomicPageRequest request, CancellationToken cancellationToken);
}

using System.Linq.Expressions;
using EConomic.Querying;

namespace EConomic.Open;

/// <summary>
/// One page of a cursor listing, and the cursor that continues it.
/// </summary>
/// <typeparam name="T">The resource type.</typeparam>
/// <param name="Items">The items in this page.</param>
/// <param name="Cursor">
/// The continuation cursor, or <see langword="null"/> when there is nothing more. e-conomic omits
/// the cursor from the response entirely once the listing is exhausted, so its absence — rather
/// than an empty page — is what ends an enumeration.
/// </param>
public sealed record EconomicCursorPage<T>(IReadOnlyList<T> Items, string? Cursor);

/// <summary>
/// Fetches from one collection of an OpenAPI service.
/// </summary>
/// <typeparam name="T">The resource type.</typeparam>
/// <remarks>
/// The three listing endpoints every collection publishes, behind one interface. The query decides
/// which to use; a resource implements this once over its generated client.
/// </remarks>
internal interface IEconomicOpenSource<T>
{
    /// <summary>
    /// Whether the collection publishes a cursor listing.
    /// </summary>
    /// <remarks>
    /// Most do, and it is the listing to prefer. A few publish only the classic one — the accounting
    /// years collection among them — and enumerating those has to page instead.
    /// </remarks>
    bool CanCursor { get; }

    /// <summary>
    /// Whether the collection publishes the classic paged listing.
    /// </summary>
    /// <remarks>
    /// Sorting exists only there, so a collection without it cannot be ordered at all — the sort
    /// surface for one is empty, and the resource offers no <c>OrderBy</c>.
    /// </remarks>
    bool CanPage { get; }

    /// <summary>Fetches a cursor page. Sorting is not available here — the server ignores it.</summary>
    Task<EconomicCursorPage<T>> GetCursorPageAsync(string? cursor, string? filter, CancellationToken cancellationToken);

    /// <summary>Fetches a numbered page, which is the only listing that sorts.</summary>
    Task<IReadOnlyList<T>> GetPageAsync(
        string? filter, string? sort, int pageSize, int skipPages, CancellationToken cancellationToken);

    /// <summary>Counts what the filter matches.</summary>
    Task<int> CountAsync(string? filter, CancellationToken cancellationToken);
}

/// <summary>
/// A composable query over a collection of an OpenAPI service.
/// </summary>
/// <typeparam name="TResource">The resource type returned.</typeparam>
/// <typeparam name="TFilter">The generated filter surface.</typeparam>
/// <typeparam name="TSort">The generated sort surface.</typeparam>
/// <remarks>
/// <para>
/// Separate from the legacy <see cref="EconomicQuery{TResource, TFilter, TSort}"/> because the
/// two surfaces page differently: this one prefers a cursor, which has no page numbers and no
/// total, and falls back to classic paging when it must. The filter and sort <em>syntax</em> is
/// shared — both surfaces speak the same <c>$eq:</c> and <c>$and:</c>, and both use the same
/// escaping — so the translation is the same code, and only the paging differs.
/// </para>
/// <para>
/// Queries are immutable. Each method returns a new query, so one can be built up once and reused
/// as the starting point for several.
/// </para>
/// </remarks>
public sealed class EconomicOpenQuery<TResource, TFilter, TSort>
{
    /// <summary>
    /// The largest page the classic endpoint serves.
    /// </summary>
    /// <remarks>
    /// A hundred, not the thousand the documentation quotes for these services: the specification
    /// declares <c>pageSize</c> with a maximum of 100 and the server enforces it — "The field
    /// PageSize must be between 1 and 100". The thousand applies to a cursor listing, which has no
    /// page-size parameter at all and returns up to that many on its own.
    /// </remarks>
    public const int MaxPageSize = 100;

    /// <summary>
    /// The most items classic paging can reach. Beyond this the server stops returning results, so
    /// a sorted enumeration cannot go further.
    /// </summary>
    public const int ClassicPagingLimit = 10_000;

    private readonly IEconomicOpenSource<TResource> _source;
    private readonly string? _filter;
    private readonly string? _sort;
    private readonly int _pageSize;

    internal EconomicOpenQuery(IEconomicOpenSource<TResource> source)
        : this(source, filter: null, sort: null, pageSize: MaxPageSize)
    {
    }

    private EconomicOpenQuery(IEconomicOpenSource<TResource> source, string? filter, string? sort, int pageSize)
    {
        _source = source;
        _filter = filter;
        _sort = sort;
        _pageSize = pageSize;
    }

    /// <summary>Restricts what is returned.</summary>
    /// <param name="predicate">A filter over the filterable properties.</param>
    /// <returns>A query carrying the filter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public EconomicOpenQuery<TResource, TFilter, TSort> Where(Expression<Func<TFilter, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        return WhereRaw(FilterTranslator.Translate(predicate));
    }

    /// <summary>Restricts what is returned, using e-conomic's filter syntax directly.</summary>
    /// <param name="filter">A filter expression.</param>
    /// <returns>A query carrying the filter.</returns>
    /// <remarks>
    /// The escape hatch for a property the specification does not admit is filterable. On this
    /// surface that is rarer than on the legacy one — these services publish an operator list per
    /// property — but the server remains the authority, not the document.
    /// </remarks>
    public EconomicOpenQuery<TResource, TFilter, TSort> WhereRaw(string filter) =>
        new(_source, Combine(_filter, filter), _sort, _pageSize);

    /// <summary>Orders ascending.</summary>
    /// <param name="selector">The property to sort by.</param>
    /// <returns>A query carrying the sort.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public EconomicOpenQuery<TResource, TFilter, TSort> OrderBy(Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Ascending, replace: true);

    /// <summary>Orders descending.</summary>
    /// <param name="selector">The property to sort by.</param>
    /// <returns>A query carrying the sort.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public EconomicOpenQuery<TResource, TFilter, TSort> OrderByDescending(
        Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Descending, replace: true);

    /// <summary>Adds a further ordering.</summary>
    /// <param name="selector">The property to sort by.</param>
    /// <returns>A query carrying both orderings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public EconomicOpenQuery<TResource, TFilter, TSort> ThenBy(Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Ascending, replace: false);

    /// <summary>Adds a further descending ordering.</summary>
    /// <param name="selector">The property to sort by.</param>
    /// <returns>A query carrying both orderings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    public EconomicOpenQuery<TResource, TFilter, TSort> ThenByDescending(
        Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Descending, replace: false);

    /// <summary>Sets how many items are fetched per request from the classic endpoint.</summary>
    /// <param name="pageSize">Items per page, from 1 to <see cref="MaxPageSize"/>.</param>
    /// <returns>A query using that page size.</returns>
    /// <remarks>
    /// This has no effect on a cursor listing, which takes no page size — the server decides, up to
    /// a thousand at a time.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The page size is outside what e-conomic serves.</exception>
    public EconomicOpenQuery<TResource, TFilter, TSort> WithPageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);

        return new(_source, _filter, _sort, pageSize);
    }

    /// <summary>Counts what this query matches, without fetching it.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The number of matching records.</returns>
    /// <remarks>
    /// The OpenAPI services publish a <c>/count</c> for every collection, which the legacy API does
    /// not — there, counting means paging through the results.
    /// </remarks>
    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _source.CountAsync(_filter, cancellationToken);

    /// <summary>
    /// Enumerates everything the query matches, fetching as it is consumed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <returns>The items.</returns>
    /// <remarks>
    /// <para>
    /// An unsorted enumeration follows the cursor, which has no limit on how far it reaches. Adding
    /// an ordering switches it to classic paging, because <strong>the server silently ignores
    /// <c>sort</c> on a cursor request</strong> — it answers <c>200</c> with unsorted data, verified
    /// against a live agreement. Issuing that would quietly return the wrong order.
    /// </para>
    /// <para>
    /// A collection that publishes no cursor listing pages classically throughout, and one that
    /// publishes no classic listing can only be enumerated unsorted — which is why neither offers
    /// an ordering to apply in the first place.
    /// </para>
    /// <para>
    /// The switch has a cost worth knowing: classic paging stops after
    /// <see cref="ClassicPagingLimit"/> items, so a sorted enumeration of a larger collection ends
    /// there. Sorting a collection that big is better done by filtering it down first.
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<TResource> AsAsyncEnumerable(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_sort is null && _source.CanCursor)
        {
            string? cursor = null;

            do
            {
                var page = await _source.GetCursorPageAsync(cursor, _filter, cancellationToken).ConfigureAwait(false);

                foreach (var item in page.Items)
                {
                    yield return item;
                }

                cursor = page.Cursor;
            }
            while (cursor is not null);

            yield break;
        }

        for (var skipPages = 0; skipPages * _pageSize < ClassicPagingLimit; skipPages++)
        {
            var page = await _source
                .GetPageAsync(_filter, _sort, _pageSize, skipPages, cancellationToken)
                .ConfigureAwait(false);

            foreach (var item in page)
            {
                yield return item;
            }

            if (page.Count < _pageSize)
            {
                yield break;
            }
        }
    }

    /// <summary>Fetches one numbered page.</summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page.</returns>
    /// <remarks>
    /// Always the classic endpoint, sorted or not: page numbers only exist there. Callers who want
    /// the cursor should use <see cref="GetCursorPageAsync"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative.</exception>
    public async Task<IReadOnlyList<TResource>> GetPageAsync(
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return await _source
            .GetPageAsync(_filter, _sort, _pageSize, pageIndex, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Fetches one cursor page, for callers managing the cursor themselves.</summary>
    /// <param name="cursor">The cursor from the previous page, or <see langword="null"/> to start.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The page and the cursor that continues it.</returns>
    /// <exception cref="InvalidOperationException">The query carries an ordering, which a cursor ignores.</exception>
    public Task<EconomicCursorPage<TResource>> GetCursorPageAsync(
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        // Silently dropping the ordering is what the server does, and it is the behaviour this
        // library exists to prevent. AsAsyncEnumerable can switch endpoints because it owns the
        // paging; here the caller has asked for a cursor specifically.
        if (_sort is not null)
        {
            throw new InvalidOperationException(
                "A cursor listing cannot be sorted: e-conomic ignores sort on a cursor request and "
                + "answers with unordered data. Use GetPageAsync or AsAsyncEnumerable, which switch "
                + "to the paged endpoint when an ordering is present.");
        }

        if (!_source.CanCursor)
        {
            throw new InvalidOperationException(
                "This collection publishes no cursor listing, only the classic paged one. Use "
                + "GetPageAsync, or AsAsyncEnumerable, which pages it for you.");
        }

        return _source.GetCursorPageAsync(cursor, _filter, cancellationToken);
    }

    private EconomicOpenQuery<TResource, TFilter, TSort> WithSort(
        Expression<Func<TSort, EconomicSortField>> selector,
        SortDirection direction,
        bool replace)
    {
        var clause = new SortClause(SortTranslator.FieldName(selector), direction).ToString();

        return new(_source, _filter, replace || _sort is null ? clause : $"{_sort},{clause}", _pageSize);
    }

    private static string Combine(string? existing, string addition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addition);

        return existing is null ? addition : $"{existing}$and:{addition}";
    }
}

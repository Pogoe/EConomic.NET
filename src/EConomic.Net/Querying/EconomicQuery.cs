using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using EConomic.Pagination;

namespace EConomic.Querying;

/// <summary>
/// A composable query over an e-conomic collection.
/// </summary>
/// <typeparam name="TResource">The resource returned.</typeparam>
/// <typeparam name="TFilter">The generated filter surface for that resource.</typeparam>
/// <typeparam name="TSort">The generated sort surface for that resource.</typeparam>
/// <remarks>
/// <para>
/// Deliberately not <c>IQueryable&lt;T&gt;</c>. That interface promises all of LINQ, and against an
/// API where most properties are not filterable the promise turns ordinary-looking queries into
/// runtime failures. What is offered here is what e-conomic actually supports, so anything that
/// compiles can be translated.
/// </para>
/// <para>
/// Each method returns a new query rather than mutating this one, so a partially built query can
/// be shared or reused without surprises.
/// </para>
/// </remarks>
[SuppressMessage("Major Code Smell", "S2436:Types and methods should not have too many generic parameters",
    Justification = "The three are the point: the resource, its generated filter surface and its generated sort "
        + "surface are independent per endpoint, and carrying each as its own parameter is what makes a "
        + "non-filterable property a compile error instead of a 400.")]
public sealed class EconomicQuery<TResource, TFilter, TSort>
{
    /// <summary>e-conomic's default page size.</summary>
    public const int DefaultPageSize = 20;

    /// <summary>The largest page size e-conomic accepts.</summary>
    public const int MaxPageSize = 1000;

    private readonly IEconomicPageSource<TResource> _source;
    private readonly IReadOnlyList<string> _filters;
    private readonly IReadOnlyList<SortClause> _sorts;
    private readonly int _pageSize;

    internal EconomicQuery(IEconomicPageSource<TResource> source)
        : this(source, [], [], DefaultPageSize)
    {
    }

    private EconomicQuery(
        IEconomicPageSource<TResource> source,
        IReadOnlyList<string> filters,
        IReadOnlyList<SortClause> sorts,
        int pageSize)
    {
        _source = source;
        _filters = filters;
        _sorts = sorts;
        _pageSize = pageSize;
    }

    /// <summary>Restricts the query. Repeated calls are combined with <c>$and:</c>.</summary>
    /// <param name="predicate">A predicate over the resource's filter surface.</param>
    /// <returns>A new query including this restriction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The predicate uses something e-conomic cannot express.</exception>
    public EconomicQuery<TResource, TFilter, TSort> Where(Expression<Func<TFilter, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return WhereRaw(FilterTranslator.Translate(predicate));
    }

    /// <summary>
    /// Restricts the query using a filter expression written by hand.
    /// </summary>
    /// <param name="filter">A filter expression, e.g. <c>pNumber$eq:1234567890</c>.</param>
    /// <returns>A new query including this restriction.</returns>
    /// <remarks>
    /// The escape hatch for fields the published schema under-reports. On Customers the schema
    /// marks 20 properties filterable while the server accepts 21 — it omits <c>pNumber</c> — so
    /// the generated surface cannot be assumed complete. Nothing is escaped here; the caller owns
    /// the syntax. When it is wrong, the server's 400 lists the fields it does accept, surfaced as
    /// <see cref="Exceptions.EconomicApiException.AllowedFilteringFields"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="filter"/> is empty.</exception>
    public EconomicQuery<TResource, TFilter, TSort> WhereRaw(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        var filters = new List<string>(_filters) { filter };
        return new EconomicQuery<TResource, TFilter, TSort>(_source, filters, _sorts, _pageSize);
    }

    /// <summary>Orders ascending, replacing any existing ordering.</summary>
    /// <param name="selector">The field to order by.</param>
    /// <returns>A new query with this ordering.</returns>
    public EconomicQuery<TResource, TFilter, TSort> OrderBy(Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Ascending, replace: true);

    /// <summary>Orders descending, replacing any existing ordering.</summary>
    /// <param name="selector">The field to order by.</param>
    /// <returns>A new query with this ordering.</returns>
    public EconomicQuery<TResource, TFilter, TSort> OrderByDescending(
        Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Descending, replace: true);

    /// <summary>Adds a secondary ascending ordering.</summary>
    /// <param name="selector">The field to order by.</param>
    /// <returns>A new query with this ordering appended.</returns>
    /// <exception cref="InvalidOperationException">No ordering has been established yet.</exception>
    public EconomicQuery<TResource, TFilter, TSort> ThenBy(Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Ascending, replace: false);

    /// <summary>Adds a secondary descending ordering.</summary>
    /// <param name="selector">The field to order by.</param>
    /// <returns>A new query with this ordering appended.</returns>
    /// <exception cref="InvalidOperationException">No ordering has been established yet.</exception>
    public EconomicQuery<TResource, TFilter, TSort> ThenByDescending(
        Expression<Func<TSort, EconomicSortField>> selector) =>
        WithSort(selector, SortDirection.Descending, replace: false);

    /// <summary>Sets how many items each request fetches.</summary>
    /// <param name="pageSize">Items per request, from 1 to <see cref="MaxPageSize"/>.</param>
    /// <returns>A new query using this page size.</returns>
    /// <remarks>
    /// Affects how the collection is fetched, not how much of it is returned: enumeration still
    /// yields every match. Larger pages mean fewer calls, and e-conomic charges per call.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is out of range.</exception>
    public EconomicQuery<TResource, TFilter, TSort> WithPageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, MaxPageSize);

        return new EconomicQuery<TResource, TFilter, TSort>(_source, _filters, _sorts, pageSize);
    }

    /// <summary>The <c>filter=</c> value this query will send, or <see langword="null"/> for none.</summary>
    /// <returns>The filter expression.</returns>
    public string? GetFilterExpression() =>
        _filters.Count == 0 ? null : string.Join("$and:", _filters);

    /// <summary>The <c>sort=</c> value this query will send, or <see langword="null"/> for none.</summary>
    /// <returns>The sort expression.</returns>
    public string? GetSortExpression() =>
        _sorts.Count == 0 ? null : SortTranslator.Render(_sorts);

    /// <summary>Fetches a single page.</summary>
    /// <param name="pageIndex">Zero-based page number.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The page.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative.</exception>
    public Task<EconomicPage<TResource>> GetPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return _source.GetPageAsync(
            new EconomicPageRequest(GetFilterExpression(), GetSortExpression(), pageIndex, _pageSize),
            cancellationToken);
    }

    /// <summary>Enumerates every match, fetching pages as needed.</summary>
    /// <param name="cancellationToken">Token to stop enumerating.</param>
    /// <returns>The matching resources.</returns>
    /// <remarks>
    /// Pages are fetched lazily, so breaking out of the loop early stops the calls. Nothing is
    /// buffered: the whole collection is never held in memory at once.
    /// </remarks>
    public async IAsyncEnumerable<TResource> AsAsyncEnumerable(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pageIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await GetPageAsync(pageIndex, cancellationToken).ConfigureAwait(false);

            foreach (var item in page.Items)
            {
                yield return item;
            }

            if (!page.HasMore)
            {
                yield break;
            }

            pageIndex++;
        }
    }

    private EconomicQuery<TResource, TFilter, TSort> WithSort(
        Expression<Func<TSort, EconomicSortField>> selector,
        SortDirection direction,
        bool replace)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (!replace && _sorts.Count == 0)
        {
            throw new InvalidOperationException(
                "ThenBy requires an existing ordering. Call OrderBy or OrderByDescending first.");
        }

        var clause = new SortClause(SortTranslator.FieldName(selector), direction);
        var sorts = replace ? [clause] : new List<SortClause>(_sorts) { clause };

        return new EconomicQuery<TResource, TFilter, TSort>(_source, _filters, sorts, _pageSize);
    }
}

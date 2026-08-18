using EConomic.Open;
using EConomic.Querying;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Composition rules for the OpenAPI-service query, and specifically the ones it has to share with
/// <see cref="EconomicQuery{TResource, TFilter, TSort}"/>.
/// </summary>
/// <remarks>
/// The two queries are separate types because the surfaces page differently, which is a real reason
/// for the split and not a licence for the shared half to drift. <c>ThenBy</c> did drift: the legacy
/// query rejected one with no ordering to follow while this one silently promoted it to the primary
/// ordering, and nothing compared them.
/// </remarks>
public class EconomicOpenQueryTests
{
    private static EconomicOpenQuery<string, CustomerFilter, CustomerSort> Query() =>
        new(new FakeSource());

    [Fact]
    public void Then_by_without_an_ordering_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Query().ThenBy(c => c.CustomerNumber));

        Assert.Contains("OrderBy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Then_by_descending_without_an_ordering_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => Query().ThenByDescending(c => c.CustomerNumber));

    [Fact]
    public void Both_queries_reject_it_with_the_same_message()
    {
        // The rule is shared, so the wording is too — a caller moving between the surfaces should
        // not have to learn it twice.
        var open = Assert.Throws<InvalidOperationException>(() => Query().ThenBy(c => c.CustomerNumber));
        var legacy = Assert.Throws<InvalidOperationException>(
            () => new EconomicQuery<string, Rest.CustomerFilter, Rest.CustomerSort>(new FakeLegacySource())
                .ThenBy(c => c.Name));

        Assert.Equal(legacy.Message, open.Message, StringComparer.Ordinal);
    }

    [Fact]
    public void Order_by_still_replaces_and_then_by_still_appends()
    {
        // The guard must not have cost the behaviour it guards.
        var query = Query()
            .OrderBy(c => c.CustomerNumber)
            .ThenByDescending(c => c.CustomerNumber);

        Assert.Throws<InvalidOperationException>(() => Query().ThenBy(c => c.CustomerNumber));
        Assert.NotNull(query);
    }

    [Fact]
    public void A_null_selector_is_rejected_before_anything_else() =>
        Assert.Throws<ArgumentNullException>(() => Query().OrderBy(null!));

    private sealed class FakeSource : IEconomicOpenSource<string>
    {
        public bool CanCursor => true;

        public bool CanPage => true;

        public Task<EconomicCursorPage<string>> GetCursorPageAsync(
            string? cursor, string? filter, CancellationToken cancellationToken) =>
            Task.FromResult(new EconomicCursorPage<string>([], null));

        public Task<IReadOnlyList<string>> GetPageAsync(
            string? filter, string? sort, int pageSize, int skipPages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<int> CountAsync(string? filter, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class FakeLegacySource : Pagination.IEconomicPageSource<string>
    {
        public Task<Pagination.EconomicPage<string>> GetPageAsync(
            Pagination.EconomicPageRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new Pagination.EconomicPage<string>([], request.PageIndex, request.PageSize));
    }
}

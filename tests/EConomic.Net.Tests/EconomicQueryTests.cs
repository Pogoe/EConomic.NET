using EConomic.Pagination;
using EConomic.Querying;
using EConomic.Rest;
using Xunit;

namespace EConomic.Tests;

public class EconomicQueryTests
{
    private static EconomicQuery<string, CustomerFilter, CustomerSort> Query(FakePageSource source) => new(source);

    private static EconomicQuery<string, CustomerFilter, CustomerSort> Query(params int[] pageSizes) =>
        new(new FakePageSource(pageSizes));

    [Fact]
    public void A_query_starts_with_no_filter_and_no_sort()
    {
        var query = Query(0);

        Assert.Null(query.GetFilterExpression());
        Assert.Null(query.GetSortExpression());
    }

    [Fact]
    public void Repeated_where_calls_are_combined_with_and()
    {
        var query = Query(0)
            .Where(c => c.Name == "Joe")
            .Where(c => c.CustomerNumber > 10);

        Assert.Equal("name$eq:Joe$and:customerNumber$gt:10", query.GetFilterExpression());
    }

    [Fact]
    public void Raw_filters_compose_with_translated_ones()
    {
        // The escape hatch for fields the schema under-reports, such as pNumber.
        var query = Query(0)
            .Where(c => c.Name == "Joe")
            .WhereRaw("pNumber$eq:1234567890");

        Assert.Equal("name$eq:Joe$and:pNumber$eq:1234567890", query.GetFilterExpression());
    }

    [Fact]
    public void Order_by_replaces_but_then_by_appends()
    {
        var query = Query(0)
            .OrderBy(c => c.Name)
            .ThenByDescending(c => c.City);

        Assert.Equal("name,-city", query.GetSortExpression());

        var reordered = query.OrderByDescending(c => c.CustomerNumber);

        Assert.Equal("-customerNumber", reordered.GetSortExpression());
    }

    [Fact]
    public void Then_by_without_an_ordering_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Query(0).ThenBy(c => c.Name));

        Assert.Contains("OrderBy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Composition_does_not_mutate_the_original_query()
    {
        var original = Query(0).Where(c => c.Name == "Joe");

        _ = original.Where(c => c.City == "Aarhus").OrderBy(c => c.Name);

        Assert.Equal("name$eq:Joe", original.GetFilterExpression());
        Assert.Null(original.GetSortExpression());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(EconomicQuery<string, CustomerFilter, CustomerSort>.MaxPageSize + 1)]
    public void Page_size_is_validated_against_what_the_server_accepts(int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Query(0).WithPageSize(pageSize));

    [Fact]
    public async Task Enumeration_walks_every_page()
    {
        // Three full pages then a short one, which is what ends the walk.
        var source = new FakePageSource(20, 20, 20, 7);
        var query = Query(source);

        var items = new List<string>();
        await foreach (var item in query.AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal(67, items.Count);
        Assert.Equal(4, source.Requests.Count);
        Assert.Equal([0, 1, 2, 3], source.Requests.Select(r => r.PageIndex));
    }

    [Fact]
    public async Task A_full_last_page_costs_one_extra_empty_request()
    {
        // Nothing in the response says "this was the last page", so a page that happens to be full
        // is followed by one more call that comes back empty.
        var source = new FakePageSource(20, 0);

        var items = new List<string>();
        await foreach (var item in Query(source).AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        Assert.Equal(20, items.Count);
        Assert.Equal(2, source.Requests.Count);
    }

    [Fact]
    public async Task Breaking_out_early_stops_fetching()
    {
        var source = new FakePageSource(20, 20, 20);

        var seen = 0;
        await foreach (var _ in Query(source).AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            if (++seen == 5)
            {
                break;
            }
        }

        Assert.Equal(5, seen);
        Assert.Single(source.Requests);
    }

    [Fact]
    public async Task Filter_and_sort_reach_the_transport()
    {
        var source = new FakePageSource(0);

        await Query(source)
            .Where(c => c.Name.Like("Acme*"))
            .OrderByDescending(c => c.CustomerNumber)
            .WithPageSize(500)
            .GetPageAsync(2, TestContext.Current.CancellationToken);

        var request = Assert.Single(source.Requests);
        Assert.Equal("name$like:Acme*", request.Filter);
        Assert.Equal("-customerNumber", request.Sort);
        Assert.Equal(2, request.PageIndex);
        Assert.Equal(500, request.PageSize);
    }

    [Fact]
    public async Task Enumeration_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakePageSource(20, 20, 20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Query(source).AsAsyncEnumerable(cancellation.Token))
            {
                await cancellation.CancelAsync();
            }
        });
    }

    [Fact]
    public async Task A_negative_page_index_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Query(0).GetPageAsync(-1, TestContext.Current.CancellationToken));

    /// <summary>Returns pages of the given sizes, recording what was asked for.</summary>
    private sealed class FakePageSource(params int[] pageSizes) : IEconomicPageSource<string>
    {
        public List<EconomicPageRequest> Requests { get; } = [];

        public Task<EconomicPage<string>> GetPageAsync(
            EconomicPageRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);

            var count = request.PageIndex < pageSizes.Length ? pageSizes[request.PageIndex] : 0;
            var items = Enumerable.Range(0, count).Select(i => $"item-{request.PageIndex}-{i}").ToList();

            return Task.FromResult(new EconomicPage<string>(items, request.PageIndex, request.PageSize));
        }
    }
}

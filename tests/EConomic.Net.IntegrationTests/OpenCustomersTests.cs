using EConomic.Exceptions;
using EConomic.Open;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Exercises the customers OpenAPI service against a live agreement.
/// </summary>
/// <remarks>
/// The behaviour that differs from the legacy surface is the point of these: the caller supplies
/// the identifier, a create answers with that identifier and nothing else, an update carries an
/// <c>objectVersion</c> or is refused, and a cursor listing ignores any sort it is given.
/// </remarks>
public class OpenCustomersTests
{
    [Fact]
    public async Task A_customer_survives_a_create_read_update_delete_round_trip()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;
        const int number = 5100;
        var created = false;

        try
        {
            // Unlike the legacy surface, the number is the caller's to choose: omitting it fails
            // with "The field CustomerNumber must be between 1 and 999999999".
            var assigned = await client.Open.Customers.CreateAsync(
                new Customer
                {
                    CustomerNumber = number,
                    Name = "ZZ Probe Open",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermId = 1,
                    Zone = 1,
                },
                token);

            created = true;

            // The create response carries the identifier and nothing else.
            Assert.Equal(number, assigned);

            var read = await client.Open.Customers.GetAsync(number, token);

            Assert.Equal("ZZ Probe Open", read.Name);
            Assert.False(string.IsNullOrEmpty(read.ObjectVersion));

            // An update has to carry the objectVersion that was just read. Without it the server
            // refuses, which is a different failure from anything the legacy surface produces.
            await client.Open.Customers.UpdateAsync(
                number,
                read with { Name = "ZZ Probe Open (updated)" },
                token);

            var updated = await client.Open.Customers.GetAsync(number, token);

            Assert.Equal("ZZ Probe Open (updated)", updated.Name);
            Assert.NotEqual(read.ObjectVersion, updated.ObjectVersion);
        }
        finally
        {
            if (created)
            {
                await client.Open.Customers.DeleteAsync(number, token);
            }
        }
    }

    [Fact]
    public async Task An_update_without_the_object_version_is_reported_as_a_conflict()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;
        const int number = 5101;
        var created = false;

        try
        {
            await client.Open.Customers.CreateAsync(
                new Customer
                {
                    CustomerNumber = number,
                    Name = "ZZ Probe Conflict",
                    Currency = "DKK",
                    CustomerGroupNumber = 1,
                    PaymentTermId = 1,
                    Zone = 1,
                },
                token);

            created = true;
            var read = await client.Open.Customers.GetAsync(number, token);

            // Optimistic concurrency: no version at all is treated exactly like a stale one.
            var conflict = await Assert.ThrowsAsync<EconomicConcurrencyException>(
                () => client.Open.Customers.UpdateAsync(
                    number,
                    read with { Name = "ZZ Probe Conflict (stale)", ObjectVersion = null },
                    token));

            Assert.Equal(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Contains("UpdateConflict", conflict.RawBody ?? string.Empty, StringComparison.Ordinal);

            // And nothing was written.
            var unchanged = await client.Open.Customers.GetAsync(number, token);
            Assert.Equal("ZZ Probe Conflict", unchanged.Name);
        }
        finally
        {
            if (created)
            {
                await client.Open.Customers.DeleteAsync(number, token);
            }
        }
    }

    [Fact]
    public async Task Counting_filtering_and_paging_agree_with_each_other()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        var total = await client.Open.Customers.CountAsync(token);
        Assert.True(total >= 0);

        // The cursor listing is the default, and enumerating it must reach the same number of
        // records the service counts.
        var enumerated = 0;
        await foreach (var customer in client.Open.Customers.AsAsyncEnumerable(token))
        {
            Assert.NotNull(customer.CustomerNumber);
            enumerated++;
        }

        Assert.Equal(total, enumerated);

        // customerNumber is the one property this service will filter on: 54 of the 55 are marked
        // "not filterable", which is why the filter surface is nearly empty and why an IQueryable
        // would have been a promise the API cannot keep.
        var filtered = await client.Open.Customers
            .Where(c => c.CustomerNumber > 0)
            .CountAsync(token);

        Assert.Equal(total, filtered);
    }

    [Fact]
    public async Task Sorting_moves_the_query_onto_the_paged_endpoint()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        var descending = new List<int?>();
        await foreach (var customer in client.Open.Customers
            .OrderByDescending(c => c.CustomerNumber)
            .AsAsyncEnumerable(token))
        {
            descending.Add(customer.CustomerNumber);
        }

        // The server accepts sort only on the paged endpoint and silently ignores it on a cursor
        // request, so this ordering is evidence that the query switched endpoints.
        Assert.Equal(descending.OrderByDescending(n => n), descending);

        // Asking for a cursor page explicitly while sorted is refused rather than quietly returning
        // unordered data, which is what the server would do.
        var query = client.Open.Customers.OrderBy(c => c.CustomerNumber);

        await Assert.ThrowsAsync<InvalidOperationException>(() => query.GetCursorPageAsync(cursor: null, token));
    }
}

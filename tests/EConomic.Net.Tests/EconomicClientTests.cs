using System.Net;
using System.Net.Http;
using System.Text;
using EConomic.Authentication;
using EConomic.Exceptions;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Drives the whole slice — query composition, transport, mapping — against a stub transport.
/// </summary>
public class EconomicClientTests
{
    private const string CustomersJson = """
        {
          "collection": [
            {
              "customerNumber": 1,
              "name": "Decathlon",
              "currency": "DKK",
              "city": "Brussels",
              "zip": "1040",
              "balance": -1600.50,
              "dueAmount": 0,
              "creditLimit": 0,
              "barred": false,
              "email": "customerone@mailinator.com",
              "lastUpdated": "2026-08-01T09:30:00Z",
              "customerGroup": { "customerGroupNumber": 1, "self": "https://restapi.e-conomic.com/customer-groups/1" },
              "paymentTerms": { "paymentTermsNumber": 3, "self": "https://restapi.e-conomic.com/payment-terms/3" },
              "self": "https://restapi.e-conomic.com/customers/1"
            }
          ],
          "pagination": { "maxPageSizeAllowed": 1000, "skipPages": 0, "pageSize": 20, "results": 1 },
          "self": "https://restapi.e-conomic.com/customers"
        }
        """;

    private const string LegacyErrorJson = """
        {
          "message": "Could not parse query string filter.",
          "logId": "test-log-id",
          "httpStatusCode": 400,
          "errors": ["Filtering is not allowed on property 'bogusProp'."],
          "allowedFilteringFields": ["customerNumber", "name"]
        }
        """;

    [Fact]
    public async Task A_query_reaches_the_wire_as_filter_and_sort_parameters()
    {
        var handler = new StubHandler(HttpStatusCode.OK, CustomersJson);
        var client = CreateClient(handler);

        _ = await client.Rest.Customers
            .Where(c => c.CustomerNumber > 1000 && c.Name.Like("Acme*"))
            .OrderByDescending(c => c.CustomerNumber)
            .WithPageSize(50)
            .GetPageAsync(0, TestContext.Current.CancellationToken);

        // Assert the decoded values: how the transport percent-encodes them is its business, but
        // what e-conomic ends up parsing is ours.
        var parameters = ParseQuery(handler.LastRequest!.RequestUri!);

        Assert.Equal("customerNumber$gt:1000$and:name$like:Acme*", parameters["filter"]);
        Assert.Equal("-customerNumber", parameters["sort"]);
        Assert.Equal("50", parameters["pagesize"]);
        Assert.Equal("0", parameters["skippages"]);
    }

    [Fact]
    public async Task Responses_are_mapped_onto_the_public_model()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.OK, CustomersJson));

        var customers = new List<Rest.Customer>();
        await foreach (var customer in client.Rest.Customers.AsAsyncEnumerable(TestContext.Current.CancellationToken))
        {
            customers.Add(customer);
        }

        var only = Assert.Single(customers);
        Assert.Equal(1, only.CustomerNumber);
        Assert.Equal("Decathlon", only.Name);
        Assert.Equal("Brussels", only.City);

        // Money is decimal on the facade even though the generated layer uses double.
        Assert.Equal(-1600.50m, only.Balance);
        // lastUpdated is a full timestamp despite the schema calling it a date.
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 9, 30, 0, TimeSpan.Zero), only.LastUpdated);

        // Every embedded link collapses to one reference type.
        Assert.Equal(1, only.CustomerGroup!.Number);
        Assert.Equal(3, only.PaymentTerms!.Number);
        Assert.Equal(new Uri("https://restapi.e-conomic.com/customers/1"), only.Self);
    }

    [Fact]
    public async Task A_failure_surfaces_as_the_library_exception_not_the_generated_one()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.BadRequest, LegacyErrorJson));

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("test-log-id", exception.TraceId);
        Assert.Contains("bogusProp", exception.Message, StringComparison.Ordinal);
        Assert.Contains("customerNumber", exception.AllowedFilteringFields);
    }

    [Fact]
    public async Task Rate_limiting_surfaces_as_its_own_exception()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.TooManyRequests, "{}"));

        await Assert.ThrowsAsync<EconomicRateLimitException>(
            () => client.Rest.Customers.GetPageAsync(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_non_di_constructor_configures_the_supplied_client()
    {
        using var httpClient = new HttpClient(new StubHandler(HttpStatusCode.OK, CustomersJson));

        _ = new EconomicClient(httpClient, EconomicOptions.Demo());

        Assert.Equal(EconomicOptions.DefaultRestApiBaseAddress, httpClient.BaseAddress);
        Assert.Equal(
            EconomicOptions.DemoToken,
            Assert.Single(httpClient.DefaultRequestHeaders.GetValues(
                EconomicAuthenticationHandler.AppSecretTokenHeader)));
    }

    [Fact]
    public void The_non_di_constructor_rejects_incomplete_options()
    {
        using var httpClient = new HttpClient(new StubHandler(HttpStatusCode.OK, CustomersJson));

        Assert.Throws<InvalidOperationException>(() => new EconomicClient(httpClient, new EconomicOptions()));
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                parameters[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return parameters;
    }

    private static EconomicClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler), EconomicOptions.Demo());

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}

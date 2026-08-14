using System.Net;
using System.Text;
using System.Text.Json;
using EConomic;
using EConomic.Authentication;
using EConomic.Exceptions;
using EConomic.Rest;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Covers create, update and delete on <c>/customers</c>.
/// </summary>
/// <remarks>
/// These are unit tests against a stub transport because the demo agreement rejects writes, so
/// unlike the read paths there is no live test that can confirm the request is shaped correctly.
/// They therefore assert on the actual bytes sent, not just that a call was made.
/// </remarks>
public class CustomerWriteTests
{
    // Shaped after what a live agreement actually returns from POST /customers: the whole resource,
    // including `self` and the assigned number, neither of which the create schema declares.
    private const string CreatedResponse = """
        {
          "customerNumber": 42,
          "name": "Acme A/S",
          "currency": "DKK",
          "country": "Denmark",
          "customerGroup": { "customerGroupNumber": 1, "self": "https://restapi.e-conomic.com/customer-groups/1" },
          "paymentTerms": { "paymentTermsNumber": 2, "self": "https://restapi.e-conomic.com/payment-terms/2" },
          "vatZone": { "vatZoneNumber": 3, "self": "https://restapi.e-conomic.com/vat-zones/3" },
          "self": "https://restapi.e-conomic.com/customers/42"
        }
        """;

    private static CustomerCreate NewCustomer() => new()
    {
        Name = "Acme A/S",
        Currency = "DKK",
        CustomerGroupNumber = 1,
        PaymentTermsNumber = 2,
        VatZoneNumber = 3,
    };

    [Fact]
    public async Task Create_posts_to_the_customers_collection()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        await client.Customers.CreateAsync(NewCustomer(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://restapi.e-conomic.com/customers", handler.LastUri?.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Create_sends_references_as_the_nested_objects_e_conomic_expects()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        await client.Customers.CreateAsync(NewCustomer(), TestContext.Current.CancellationToken);

        // The public model takes plain numbers; e-conomic wants { "customerGroupNumber": 1 }.
        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;

        Assert.Equal(1, root.GetProperty("customerGroup").GetProperty("customerGroupNumber").GetInt32());
        Assert.Equal(2, root.GetProperty("paymentTerms").GetProperty("paymentTermsNumber").GetInt32());
        Assert.Equal(3, root.GetProperty("vatZone").GetProperty("vatZoneNumber").GetInt32());
    }

    [Fact]
    public async Task Create_cannot_be_given_a_balance()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        await client.Customers.CreateAsync(NewCustomer(), TestContext.Current.CancellationToken);

        // balance is readOnly, so CustomerCreate offers no way to set it and the payload omits it
        // entirely. Optional numbers are nullable on the generated payloads precisely so an unset
        // value is left out rather than sent as 0 — e-conomic rejects `customerNumber: 0` outright,
        // since its schema declares a minimum of 1.
        Assert.Null(typeof(CustomerCreate).GetProperty("Balance"));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.False(body.RootElement.TryGetProperty("balance", out _));
        Assert.False(body.RootElement.TryGetProperty("customerNumber", out _));
    }

    [Fact]
    public async Task Create_returns_the_whole_resource_the_server_sends()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        var created = await client.Customers.CreateAsync(NewCustomer(), TestContext.Current.CancellationToken);

        // The create schema declares neither `self` nor the assigned number, but the server returns
        // both — verified live. The response is mapped as the read entity rather than the payload,
        // so nothing has to be derived.
        Assert.Equal(42, created.CustomerNumber);
        Assert.Equal(new Uri("https://restapi.e-conomic.com/customers/42"), created.Self);
        Assert.Equal("Acme A/S", created.Name);
        Assert.Equal(1, created.CustomerGroup?.Number);
        Assert.Equal(new Uri("https://restapi.e-conomic.com/customer-groups/1"), created.CustomerGroup?.Self);
    }

    [Fact]
    public async Task Update_puts_to_the_individual_customer()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreatedResponse);
        var client = CreateClient(handler);

        var update = new CustomerUpdate
        {
            Name = "Acme A/S",
            Currency = "DKK",
            CustomerGroupNumber = 1,
            PaymentTermsNumber = 2,
            VatZoneNumber = 3,
            DefaultDeliveryLocationNumber = 7,
        };

        await client.Customers.UpdateAsync(42, update, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal("https://restapi.e-conomic.com/customers/42", handler.LastUri?.GetLeftPart(UriPartial.Path));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(42, body.RootElement.GetProperty("customerNumber").GetInt32());
        Assert.Equal(
            7,
            body.RootElement.GetProperty("defaultDeliveryLocation").GetProperty("deliveryLocationNumber").GetInt32());
    }

    [Fact]
    public async Task Delete_sends_a_delete_and_accepts_no_content()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent, body: null);
        var client = CreateClient(handler);

        await client.Customers.DeleteAsync(42, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal("https://restapi.e-conomic.com/customers/42", handler.LastUri?.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Delete_surfaces_a_failure_as_an_api_exception()
    {
        const string error = """
            { "message": "Customer not found.", "errorCode": "E04010", "logId": "abc-123" }
            """;
        var handler = new RecordingHandler(HttpStatusCode.NotFound, error);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<EconomicApiException>(
            () => client.Customers.DeleteAsync(42, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    private static EconomicClient CreateClient(RecordingHandler handler) =>
        new(new HttpClient(handler), EconomicOptions.Demo());

    private sealed class RecordingHandler(HttpStatusCode status, string? body) : HttpMessageHandler
    {
        public HttpMethod? LastMethod { get; private set; }

        public Uri? LastUri { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastUri = request.RequestUri;

            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            var response = new HttpResponseMessage(status) { RequestMessage = request };

            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}

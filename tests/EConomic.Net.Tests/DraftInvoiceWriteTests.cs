using System.Net;
using System.Text;
using System.Text.Json;
using EConomic.Authentication;
using EConomic.Rest;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Covers the composite parts of a write payload: nested objects and arrays of them.
/// </summary>
/// <remarks>
/// A draft invoice is the case that needs both. Its recipient, notes and payment terms are nested
/// objects, and its lines are an array of them — none of which the facade could express until now,
/// which is why the whole family was read-only. These assert on the bytes actually sent, because
/// the ways this can be wrong are all invisible to a test that only checks a call was made.
/// </remarks>
public class DraftInvoiceWriteTests
{
    // Shaped after what the live agreement returns: the whole invoice, priced by the server.
    private const string CreatedResponse = """
        {
          "draftInvoiceNumber": 7,
          "date": "2026-08-14",
          "currency": "DKK",
          "netAmount": 200.0,
          "grossAmount": 250.0,
          "customer": { "customerNumber": 1, "self": "https://restapi.e-conomic.com/customers/1" },
          "recipient": { "name": "Acme A/S", "city": "Ringsted", "vatZone": { "vatZoneNumber": 1 } },
          "paymentTerms": { "paymentTermsNumber": 1, "daysOfCredit": 8, "paymentTermsType": "net" },
          "self": "https://restapi.e-conomic.com/invoices/drafts/7"
        }
        """;

    private static DraftInvoiceCreate NewInvoice() => new()
    {
        Date = new DateOnly(2026, 8, 14),
        Currency = "DKK",
        LayoutNumber = 21,
        CustomerNumber = 1,
        PaymentTerms = new DraftInvoiceCreatePaymentTerms { PaymentTermsNumber = 1 },
        Recipient = new DraftInvoiceCreateRecipient
        {
            Name = "Acme A/S",
            VatZoneNumber = 1,
            City = "Ringsted",
        },
        Lines =
        [
            new DraftInvoiceCreateLine
            {
                Description = "Consulting",
                Product = new DraftInvoiceCreateLineProduct { ProductNumber = "ZZ-1" },
                Quantity = 2,
                UnitNetPrice = 100,
            },
        ],
    };

    [Fact]
    public async Task Create_posts_to_the_drafts_collection()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        await client.DraftInvoices.CreateAsync(NewInvoice(), TestContext.Current.CancellationToken);

        // The collection sits one segment below a namespace, so the path is easy to get wrong.
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://restapi.e-conomic.com/invoices/drafts", handler.LastUri?.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Create_sends_nested_objects_rather_than_flattening_them()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        await client.DraftInvoices.CreateAsync(NewInvoice(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;

        var recipient = root.GetProperty("recipient");
        Assert.Equal("Acme A/S", recipient.GetProperty("name").GetString());
        Assert.Equal("Ringsted", recipient.GetProperty("city").GetString());

        // A reference inside a nested object is still sent as { "vatZoneNumber": 1 }.
        Assert.Equal(1, recipient.GetProperty("vatZone").GetProperty("vatZoneNumber").GetInt32());
    }

    [Fact]
    public async Task Create_sends_lines_as_an_array_of_objects()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        await client.DraftInvoices.CreateAsync(NewInvoice(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var line = body.RootElement.GetProperty("lines")[0];

        Assert.Equal("Consulting", line.GetProperty("description").GetString());
        Assert.Equal(2, line.GetProperty("quantity").GetDouble());
        Assert.Equal(100, line.GetProperty("unitNetPrice").GetDouble());

        // productNumber is a string on the wire even when it looks numeric.
        Assert.Equal("ZZ-1", line.GetProperty("product").GetProperty("productNumber").GetString());
    }

    [Fact]
    public async Task An_unset_number_inside_a_line_is_omitted_rather_than_sent_as_zero()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        await client.DraftInvoices.CreateAsync(NewInvoice(), TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var line = body.RootElement.GetProperty("lines")[0];

        // The server rejected the whole invoice over exactly this: lineNumber and sortKey both
        // declare a minimum of 1, so serializing an untouched 0 failed schema validation. Marking
        // optional numbers nullable has to reach inside array items, not just the top level.
        Assert.False(line.TryGetProperty("lineNumber", out _));
        Assert.False(line.TryGetProperty("sortKey", out _));
    }

    [Fact]
    public async Task An_empty_line_collection_sends_no_lines()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        var invoice = NewInvoice() with { Lines = [] };
        await client.DraftInvoices.CreateAsync(invoice, TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Empty(body.RootElement.GetProperty("lines").EnumerateArray());
    }

    [Fact]
    public async Task Create_returns_the_invoice_the_server_priced()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, CreatedResponse);
        var client = CreateClient(handler);

        var created = await client.DraftInvoices.CreateAsync(NewInvoice(), TestContext.Current.CancellationToken);

        Assert.Equal(7, created.DraftInvoiceNumber);
        Assert.Equal(new Uri("https://restapi.e-conomic.com/invoices/drafts/7"), created.Self);
        Assert.Equal(200m, created.NetAmount);
        Assert.Equal(250m, created.GrossAmount);

        // The composite properties come back mapped, not dropped.
        Assert.Equal("Acme A/S", created.Recipient?.Name);
        Assert.Equal(1, created.Recipient?.VatZone?.Number);
        Assert.Equal(8, created.PaymentTerms?.DaysOfCredit);
    }

    [Fact]
    public async Task Update_puts_to_the_individual_draft()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, CreatedResponse);
        var client = CreateClient(handler);

        await client.DraftInvoices.UpdateAsync(
            7,
            new DraftInvoiceUpdate
            {
                Date = new DateOnly(2026, 8, 15),
                Currency = "DKK",
                CustomerNumber = 1,
                Recipient = new DraftInvoiceUpdateRecipient { Name = "Acme A/S", VatZoneNumber = 1 },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.LastMethod);
        Assert.Equal(
            "https://restapi.e-conomic.com/invoices/drafts/7",
            handler.LastUri?.GetLeftPart(UriPartial.Path));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(7, body.RootElement.GetProperty("draftInvoiceNumber").GetInt32());
    }

    [Fact]
    public async Task Delete_sends_a_delete_to_the_individual_draft()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "message": "Deleted" }""");
        var client = CreateClient(handler);

        await client.DraftInvoices.DeleteAsync(7, TestContext.Current.CancellationToken);

        // Unlike the other resources this answers 200 with a body rather than 204 — verified live —
        // so the delete must accept any success status, not just No Content.
        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal(
            "https://restapi.e-conomic.com/invoices/drafts/7",
            handler.LastUri?.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Booking_posts_a_reference_to_the_draft()
    {
        const string bookedResponse = """
            {
              "bookedInvoiceNumber": 101,
              "date": "2026-08-14",
              "currency": "DKK",
              "netAmount": 200.0,
              "recipient": { "name": "Acme A/S" },
              "self": "https://restapi.e-conomic.com/invoices/booked/101"
            }
            """;

        var handler = new RecordingHandler(HttpStatusCode.Created, bookedResponse);
        var client = CreateClient(handler);

        var booked = await client.DraftInvoices.BookAsync(7, cancellationToken: TestContext.Current.CancellationToken);

        // Booking is a POST to the booked collection carrying the draft's number — not a PUT on the
        // draft, and not a create from anything describing a booked invoice.
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://restapi.e-conomic.com/invoices/booked", handler.LastUri?.GetLeftPart(UriPartial.Path));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(7, body.RootElement.GetProperty("draftInvoice").GetProperty("draftInvoiceNumber").GetInt32());

        // Omitted, so e-conomic assigns the next number itself.
        Assert.False(body.RootElement.TryGetProperty("bookWithNumber", out _));

        Assert.Equal(101, booked.BookedInvoiceNumber);
        Assert.Equal("Acme A/S", booked.Recipient?.Name);
    }

    [Theory]
    [InlineData(EconomicInvoiceDelivery.None, "none")]
    [InlineData(EconomicInvoiceDelivery.Ean, "EAN")]
    [InlineData(EconomicInvoiceDelivery.Email, "Email")]
    public async Task Booking_sends_the_delivery_method_with_the_casing_the_server_demands(
        EconomicInvoiceDelivery delivery,
        string expected)
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, """{ "bookedInvoiceNumber": 101 }""");
        var client = CreateClient(handler);

        await client.DraftInvoices.BookAsync(
            7, bookWithNumber: 500, sendBy: delivery, cancellationToken: TestContext.Current.CancellationToken);

        // "none", "EAN", "Email" — inconsistently cased on the wire, and the server is strict about
        // it, so nothing here may be derived from the C# member names.
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(expected, body.RootElement.GetProperty("sendBy").GetString());
        Assert.Equal(500, body.RootElement.GetProperty("bookWithNumber").GetInt32());
    }

    [Fact]
    public async Task Deleting_every_draft_targets_the_collection_itself()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{ "message": "Deleted" }""");
        var client = CreateClient(handler);

        await client.DraftInvoices.DeleteEveryDraftAsync(
            DraftInvoiceBulkDelete.EveryDraft, TestContext.Current.CancellationToken);

        // No identifier: the delete addresses the collection, which is what makes it dangerous.
        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal("https://restapi.e-conomic.com/invoices/drafts", handler.LastUri?.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task Deleting_every_draft_refuses_without_a_confirmation()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, null);
        var client = CreateClient(handler);

        // default(DraftInvoiceBulkDelete) is Unspecified, so an uninitialised field or a stray
        // `default` cannot reach the server. Nothing is sent.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.DraftInvoices.DeleteEveryDraftAsync(default, TestContext.Current.CancellationToken));

        Assert.Null(handler.LastMethod);
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

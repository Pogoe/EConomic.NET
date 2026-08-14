using System.Net;
using System.Text;
using EConomic.Authentication;
using EConomic.Exceptions;
using EConomic.Http;
using EConomic.Rest;
using EConomic.Rest.Generated;

namespace EConomic.AotSmokeTest;

/// <summary>
/// Runs the library's serialization paths in a natively compiled, trimmed binary.
/// </summary>
/// <remarks>
/// Every check here would pass trivially on CoreCLR. The value is that it runs after the trimmer
/// has removed everything it believes unreachable and after ILC has compiled ahead of time, so a
/// missing <c>JsonTypeInfo</c> shows up as a failure instead of as a warning nobody reads.
/// </remarks>
internal static class Program
{
    private const string CustomersJson = """
        {
          "collection": [
            {
              "customerNumber": 1,
              "name": "Demo Customer",
              "currency": "DKK",
              "city": "Copenhagen",
              "zip": "1000",
              "balance": -1600.00,
              "barred": false,
              "customerGroup": { "customerGroupNumber": 1, "self": "https://restapi.e-conomic.com/customer-groups/1" },
              "paymentTerms": { "paymentTermsNumber": 1, "self": "https://restapi.e-conomic.com/payment-terms/1" },
              "self": "https://restapi.e-conomic.com/customers/1"
            }
          ],
          "pagination": { "maxPageSizeAllowed": 1000, "skipPages": 0, "pageSize": 20, "results": 1, "resultsWithoutFilter": 1 },
          "self": "https://restapi.e-conomic.com/customers"
        }
        """;

    private const string LegacyErrorJson = """
        {
          "message": "Could not parse query string filter.",
          "developerHint": "Use the allowed operators and allowed filtering fields.",
          "logId": "aot-smoke-test",
          "httpStatusCode": 400,
          "errors": ["Filtering is not allowed on property 'bogusProp'."],
          "allowedFilteringFields": ["customerNumber", "name"]
        }
        """;

    private const string CreatedInvoiceJson = """
        {
          "draftInvoiceNumber": 7,
          "date": "2026-08-14",
          "currency": "DKK",
          "netAmount": 200.0,
          "grossAmount": 250.0,
          "recipient": { "name": "Acme A/S", "vatZone": { "vatZoneNumber": 1 } },
          "self": "https://restapi.e-conomic.com/invoices/drafts/7"
        }
        """;

    private static int _failures;

    private static async Task<int> Main()
    {
        Console.WriteLine($"AOT smoke test — runtime {Environment.Version}, 64-bit {Environment.Is64BitProcess}");

        await GeneratedClientDeserializesAResponseAsync().ConfigureAwait(false);
        await GeneratedClientSerializesACompositePayloadAsync().ConfigureAwait(false);
        await LegacyErrorBodyIsParsedAsync().ConfigureAwait(false);
        RateLimitHeadersAreParsed();
        OptionsRedactTheirTokens();

        Console.WriteLine(_failures == 0 ? "\nAll checks passed." : $"\n{_failures} check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The important one: drives a generated NSwag client through its real serialization path.
    /// </summary>
    private static async Task GeneratedClientDeserializesAResponseAsync()
    {
        using var httpClient = new HttpClient(new StubHandler(CustomersJson))
        {
            BaseAddress = EconomicOptions.DefaultRestApiBaseAddress,
        };

        var client = new CustomersClient(httpClient);
        var result = await client.GetCustomersAsync(pagesize: 1).ConfigureAwait(false);

        Check("generated client deserializes a collection", result.Collection.Count == 1);
        Check("scalar property survives trimming", result.Collection[0].CustomerNumber == 1);
        Check("string property survives trimming", result.Collection[0].Name == "Demo Customer");
        Check("nested object survives trimming", result.Collection[0].CustomerGroup?.CustomerGroupNumber == 1);
    }

    private static async Task LegacyErrorBodyIsParsedAsync()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(LegacyErrorJson, Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers"),
        };

        var exception = await EconomicApiException.FromResponseAsync(response).ConfigureAwait(false);

        Check("legacy error body is parsed", exception.LegacyError is not null);
        Check("allowedFilteringFields survives trimming", exception.AllowedFilteringFields.Count == 2);
        Check("errors array survives trimming", exception.Errors.Count == 1);
    }

    /// <summary>
    /// The write direction, which is a separate serialization path from the read one.
    /// </summary>
    /// <remarks>
    /// A draft invoice is the payload with the most structure to lose: nested objects, an array of
    /// them, and a reference inside a nested object. Serializing rather than deserializing exercises
    /// the source-generated writer, which nothing else here touches.
    /// </remarks>
    private static async Task GeneratedClientSerializesACompositePayloadAsync()
    {
        var handler = new RecordingHandler(CreatedInvoiceJson);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = EconomicOptions.DefaultRestApiBaseAddress,
        };

        var client = new EconomicClient(httpClient, EconomicOptions.Demo());

        var created = await client.DraftInvoices.CreateAsync(
            new DraftInvoiceCreate
            {
                Date = new DateOnly(2026, 8, 14),
                Currency = "DKK",
                LayoutNumber = 21,
                CustomerNumber = 1,
                PaymentTerms = new DraftInvoiceCreatePaymentTerms { PaymentTermsNumber = 1 },
                Recipient = new DraftInvoiceCreateRecipient { Name = "Acme A/S", VatZoneNumber = 1 },
                Lines =
                [
                    new DraftInvoiceCreateLine
                    {
                        Description = "Consulting",
                        Quantity = 2,
                        UnitNetPrice = 100,
                    },
                ],
            }).ConfigureAwait(false);

        var body = handler.LastBody ?? string.Empty;

        Check(
            "nested object is serialized",
            body.Contains("\"recipient\"", StringComparison.Ordinal)
            && body.Contains("Acme A/S", StringComparison.Ordinal));

        Check(
            "reference inside a nested object is serialized",
            body.Contains("\"vatZoneNumber\":1", StringComparison.Ordinal));

        Check(
            "array of objects is serialized",
            body.Contains("\"lines\":[", StringComparison.Ordinal)
            && body.Contains("Consulting", StringComparison.Ordinal));

        // The whole invoice comes back, including what the request never described.
        Check("composite write response is mapped", created.DraftInvoiceNumber == 7);
        Check("nested response object is mapped", created.Recipient?.Name == "Acme A/S");
    }

    private static void RateLimitHeadersAreParsed()
    {
        var parsed = RateLimitStatus.TryParse("token-limit-10000-per-60-seconds: 147/10000", out var status);

        Check("rate limit header is parsed", parsed && status!.Limit == 10_000 && status.Used == 147);
    }

    private static void OptionsRedactTheirTokens()
    {
        var options = new EconomicOptions { AppSecretToken = "secret-a", AgreementGrantToken = "secret-b" };

        Check("tokens stay out of ToString()", !options.ToString().Contains("secret", StringComparison.Ordinal));
    }

    private static void Check(string description, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {description}");
        if (!passed)
        {
            _failures++;
        }
    }

    /// <summary>A stub that also keeps the request body, so the write path can be inspected.</summary>
    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
    }
}

using System.Net;
using System.Text;
using EConomic.Authentication;
using EConomic.Exceptions;
using EConomic.Http;
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

    private static int _failures;

    private static async Task<int> Main()
    {
        Console.WriteLine($"AOT smoke test — runtime {Environment.Version}, 64-bit {Environment.Is64BitProcess}");

        await GeneratedClientDeserializesAResponseAsync().ConfigureAwait(false);
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

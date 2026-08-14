using System.Net;
using System.Text;
using System.Text.Json;
using EConomic.Authentication;
using EConomic.Rest;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Covers <c>/accounting-years</c>, whose write surface is a create and nothing else.
/// </summary>
/// <remarks>
/// This is the resource that does not fit the usual shape: it is identified by the year as a
/// string rather than by a number, and e-conomic publishes neither an update nor a delete for it.
/// Because the string key therefore never reaches a method signature, the resource needed no
/// special handling beyond recording what its key actually is.
/// </remarks>
public class AccountingYearWriteTests
{
    [Fact]
    public async Task An_accounting_year_is_created_from_its_dates_alone()
    {
        const string createdResponse = """
            {
              "year": "2027",
              "fromDate": "2027-01-01",
              "toDate": "2027-12-31",
              "closed": false,
              "self": "https://restapi.e-conomic.com/accounting-years/2027"
            }
            """;

        var handler = new RecordingHandler(HttpStatusCode.Created, createdResponse);
        var client = CreateClient(handler);

        var year = await client.Rest.AccountingYears.CreateAsync(
            new AccountingYearCreate
            {
                FromDate = new DateOnly(2027, 1, 1),
                ToDate = new DateOnly(2027, 12, 31),
            },
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("2027-01-01", body.RootElement.GetProperty("fromDate").GetString());
        Assert.Equal("2027-12-31", body.RootElement.GetProperty("toDate").GetString());

        // The payload describes two dates and no identifier. The server answers with the year it
        // assigned — as a string — which is why the response is mapped as the read entity.
        Assert.Equal("2027", year.Year);
        Assert.NotNull(year.Self);

        // e-conomic publishes no update or delete for an accounting year, so neither is offered.
        Assert.Null(typeof(AccountingYearResource).GetMethod("UpdateAsync"));
        Assert.Null(typeof(AccountingYearResource).GetMethod("DeleteAsync"));
    }

    private static EconomicClient CreateClient(RecordingHandler handler) =>
        new(new HttpClient(handler), EconomicOptions.Demo());

    private sealed class RecordingHandler(HttpStatusCode status, string? body) : HttpMessageHandler
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

            var response = new HttpResponseMessage(status) { RequestMessage = request };

            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}

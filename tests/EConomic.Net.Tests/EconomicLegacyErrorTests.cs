using System.Net;
using System.Net.Http;
using EConomic.Exceptions;
using Xunit;

namespace EConomic.Tests;

public class EconomicLegacyErrorTests
{
    // Captured verbatim from restapi.e-conomic.com on 2026-08-12 by filtering on an unknown
    // property. The legacy API does not return problem+json; this is its own shape.
    private const string LiveLegacyErrorJson = """
        {
          "message": "Could not parse query string filter.",
          "developerHint": "Filtering on collections can be done using the querystring parameter 'filter'.",
          "logId": "a2a282a789f20523-CPH",
          "httpStatusCode": 400,
          "errors": ["Filtering is not allowed on property 'bogusProp'."],
          "logTime": "2026-08-12T23:18:03",
          "allowedFilteringFields": ["zip", "customerNumber", "customerGroup.customerGroupNumber", "name"]
        }
        """;

    private const string LiveProblemJson = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.5",
          "title": "Resource not found.",
          "status": 404,
          "traceId": "a2a259bbe959d8d5-CPH",
          "errorCode": "ResourceNotFound"
        }
        """;

    [Fact]
    public async Task Legacy_error_body_is_parsed_and_surfaced()
    {
        using var response = CreateResponse(HttpStatusCode.BadRequest, LiveLegacyErrorJson);

        var exception = await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken);

        Assert.NotNull(exception.LegacyError);
        Assert.Null(exception.ProblemDetails);
        Assert.Equal("a2a282a789f20523-CPH", exception.TraceId);
        Assert.Equal("Could not parse query string filter.", exception.Detail);
        Assert.Contains("querystring parameter", exception.DeveloperHint, StringComparison.Ordinal);
        Assert.Equal("Filtering is not allowed on property 'bogusProp'.", Assert.Single(exception.Errors));
    }

    [Fact]
    public async Task Rejected_filter_reports_the_fields_the_resource_actually_allows()
    {
        using var response = CreateResponse(HttpStatusCode.BadRequest, LiveLegacyErrorJson);

        var exception = await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken);

        Assert.Contains("customerNumber", exception.AllowedFilteringFields);
        Assert.Contains("customerGroup.customerGroupNumber", exception.AllowedFilteringFields);
        Assert.DoesNotContain("bogusProp", exception.AllowedFilteringFields);
    }

    [Fact]
    public async Task Offending_property_is_promoted_into_the_exception_message()
    {
        using var response = CreateResponse(HttpStatusCode.BadRequest, LiveLegacyErrorJson);

        var exception = await EconomicApiException.FromResponseAsync(response, TestContext.Current.CancellationToken);

        // The lead message alone ("Could not parse query string filter.") does not say which
        // property was wrong, so the errors array has to make it into the message.
        Assert.Contains("bogusProp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_two_error_shapes_are_not_confused_for_one_another()
    {
        using var legacyResponse = CreateResponse(HttpStatusCode.BadRequest, LiveLegacyErrorJson);
        using var problemResponse = CreateResponse(HttpStatusCode.NotFound, LiveProblemJson);

        var fromLegacy = await EconomicApiException.FromResponseAsync(legacyResponse, TestContext.Current.CancellationToken);
        var fromProblem = await EconomicApiException.FromResponseAsync(problemResponse, TestContext.Current.CancellationToken);

        Assert.Null(fromLegacy.ProblemDetails);
        Assert.NotNull(fromLegacy.LegacyError);

        Assert.NotNull(fromProblem.ProblemDetails);
        Assert.Null(fromProblem.LegacyError);
        Assert.Empty(fromProblem.Errors);
        Assert.Empty(fromProblem.AllowedFilteringFields);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers"),
        };
}

using System.Net;
using System.Net.Http;
using EConomic.Authentication;
using Xunit;

namespace EConomic.Tests;

public class EconomicAuthenticationHandlerTests
{
    [Fact]
    public async Task Adds_both_token_headers_to_every_request()
    {
        var recorder = new RecordingHandler();
        using var invoker = CreateInvoker(recorder, new EconomicOptions
        {
            AppSecretToken = "app-token",
            AgreementGrantToken = "grant-token",
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers");
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("app-token", Assert.Single(recorder.LastRequest!.Headers.GetValues(EconomicAuthenticationHandler.AppSecretTokenHeader)));
        Assert.Equal("grant-token", Assert.Single(recorder.LastRequest.Headers.GetValues(EconomicAuthenticationHandler.AgreementGrantTokenHeader)));
    }

    [Fact]
    public async Task Leaves_a_caller_supplied_agreement_header_alone()
    {
        var recorder = new RecordingHandler();
        using var invoker = CreateInvoker(recorder, EconomicOptions.Demo());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://restapi.e-conomic.com/customers");
        request.Headers.TryAddWithoutValidation(EconomicAuthenticationHandler.AgreementGrantTokenHeader, "other-agreement");
        using var response = await invoker.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("other-agreement", Assert.Single(recorder.LastRequest!.Headers.GetValues(EconomicAuthenticationHandler.AgreementGrantTokenHeader)));
        Assert.Equal(EconomicOptions.DemoToken, Assert.Single(recorder.LastRequest.Headers.GetValues(EconomicAuthenticationHandler.AppSecretTokenHeader)));
    }

    [Theory]
    [InlineData("", "grant")]
    [InlineData("app", "")]
    [InlineData("   ", "grant")]
    public void Rejects_options_that_are_missing_a_token(string appToken, string grantToken)
    {
        var options = new EconomicOptions
        {
            AppSecretToken = appToken,
            AgreementGrantToken = grantToken,
        };

        Assert.Throws<InvalidOperationException>(() => new EconomicAuthenticationHandler(options));
    }

    [Fact]
    public void ToString_does_not_leak_the_tokens()
    {
        var options = new EconomicOptions
        {
            AppSecretToken = "super-secret-app-token",
            AgreementGrantToken = "super-secret-grant-token",
        };

        var text = options.ToString();

        Assert.DoesNotContain("super-secret", text, StringComparison.Ordinal);
        Assert.Contains("redacted", text, StringComparison.Ordinal);
    }

    private static HttpMessageInvoker CreateInvoker(RecordingHandler recorder, EconomicOptions options) =>
        new(new EconomicAuthenticationHandler(options) { InnerHandler = recorder });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

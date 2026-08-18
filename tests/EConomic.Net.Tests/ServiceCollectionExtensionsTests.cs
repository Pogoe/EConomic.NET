using System.Reflection;
using EConomic.Authentication;
using EConomic.DependencyInjection;
using EConomic.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace EConomic.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void The_client_resolves_with_the_configured_base_address()
    {
        using var provider = BuildProvider(options =>
        {
            options.AppSecretToken = "app";
            options.AgreementGrantToken = "grant";
        });

        var client = provider.GetRequiredService<EconomicClient>();

        Assert.NotNull(client);
        Assert.NotNull(client.Rest.Customers);
    }

    [Fact]
    public void Every_handler_in_the_pipeline_is_registered()
    {
        using var provider = BuildProvider(options =>
        {
            options.AppSecretToken = "app";
            options.AgreementGrantToken = "grant";
        });

        // A handler missing from the container surfaces here rather than as a resolution failure
        // on the first request.
        Assert.NotNull(provider.GetRequiredService<EconomicAuthenticationHandler>());
        Assert.NotNull(provider.GetRequiredService<EconomicIdempotencyHandler>());
        Assert.NotNull(provider.GetRequiredService<EconomicRetryHandler>());
    }

    [Fact]
    public void Missing_tokens_fail_at_startup_rather_than_on_the_first_call()
    {
        var services = new ServiceCollection();
        services.AddEconomicClient(options => options.AppSecretToken = "app-only");

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EconomicOptions>>().Value);

        Assert.Contains(nameof(EconomicOptions.AgreementGrantToken), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_retry_policy_is_configurable_through_the_same_callback()
    {
        using var provider = BuildProvider(options =>
        {
            options.AppSecretToken = "app";
            options.AgreementGrantToken = "grant";
            options.Retry.MaxAttempts = 5;
        });

        var options = provider.GetRequiredService<IOptions<EconomicOptions>>().Value;

        Assert.Equal(5, options.Retry.MaxAttempts);
    }

    [Fact]
    public void An_unusable_retry_policy_is_rejected_at_startup()
    {
        var services = new ServiceCollection();
        services.AddEconomicClient(options =>
        {
            options.AppSecretToken = "app";
            options.AgreementGrantToken = "grant";
            options.Retry.MaxAttempts = 0;
        });

        using var provider = services.BuildServiceProvider(validateScopes: true);

        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EconomicOptions>>().Value);

        // The rejection has to name what was actually wrong. A predicate-and-message Validate call
        // carries one fixed string, so this used to report a missing token for a retry policy that
        // was rejected several properties away — and the assertion above passed either way.
        Assert.Contains(nameof(EconomicRetryOptions.MaxAttempts), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(EconomicOptions.AppSecretToken), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_open_api_base_address_configured_here_is_the_one_the_client_uses()
    {
        // The OpenAPI services are addressed absolutely rather than through the transport's base
        // address, so the registration has to hand it over explicitly. It did not, and configuring
        // this was a silent no-op — nothing but a live request to the wrong host showed it.
        var replacement = new Uri("https://stub.example/");

        using var provider = BuildProvider(options =>
        {
            options.AppSecretToken = "app";
            options.AgreementGrantToken = "grant";
            options.OpenApiBaseAddress = replacement;
        });

        var client = provider.GetRequiredService<EconomicClient>();

        Assert.Equal(replacement, OpenApiBaseAddressOf(client));
    }

    [Fact]
    public void The_open_api_base_address_defaults_when_it_is_not_configured()
    {
        using var provider = BuildProvider(options =>
        {
            options.AppSecretToken = "app";
            options.AgreementGrantToken = "grant";
        });

        var client = provider.GetRequiredService<EconomicClient>();

        Assert.Equal(EconomicOptions.DefaultOpenApiBaseAddress, OpenApiBaseAddressOf(client));
    }

    /// <summary>
    /// Reads the address the client resolves OpenAPI service URLs against.
    /// </summary>
    /// <remarks>
    /// By reflection because the field is private and the address is deliberately not on the public
    /// surface. The alternative — driving a request through a stub handler — would assert the same
    /// wiring at the cost of depending on which resource happens to exist.
    /// </remarks>
    private static Uri OpenApiBaseAddressOf(EconomicClient client) =>
        (Uri)typeof(EconomicClient)
            .GetField("_openApiBaseAddress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

    private static ServiceProvider BuildProvider(Action<EconomicOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddEconomicClient(configure);

        return services.BuildServiceProvider(validateScopes: true);
    }
}

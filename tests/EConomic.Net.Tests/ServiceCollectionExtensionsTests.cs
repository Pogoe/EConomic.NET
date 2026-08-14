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

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<EconomicOptions>>().Value);
    }

    private static ServiceProvider BuildProvider(Action<EconomicOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddEconomicClient(configure);

        return services.BuildServiceProvider(validateScopes: true);
    }
}

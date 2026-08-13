using EConomic.Authentication;
using EConomic.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EConomic.DependencyInjection;

/// <summary>
/// Registers <see cref="EconomicClient"/> with dependency injection.
/// </summary>
public static class EconomicClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EconomicClient"/> as a typed <see cref="HttpClient"/>, with the
    /// authentication handler and the legacy REST base address already applied.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the tokens and, optionally, the base addresses.</param>
    /// <returns>
    /// The builder for the underlying client, so callers can add their own handlers — a
    /// retry policy, logging, or a stub in tests.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <example>
    /// <code>
    /// services.AddEconomicClient(options =>
    /// {
    ///     options.AppSecretToken = configuration["Economic:AppSecretToken"]!;
    ///     options.AgreementGrantToken = configuration["Economic:AgreementGrantToken"]!;
    /// });
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddEconomicClient(
        this IServiceCollection services,
        Action<EconomicOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<EconomicOptions>()
            .Configure(configure)
            // Fail at startup rather than on the first call, when the cause is far away.
            .Validate(
                options =>
                {
                    try
                    {
                        options.Validate();
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                },
                $"{nameof(EconomicOptions.AppSecretToken)} and {nameof(EconomicOptions.AgreementGrantToken)} "
                + "are both required. Create them at https://www.e-conomic.com/developer/connect.")
            .ValidateOnStart();

        services.AddTransient(provider => new EconomicAuthenticationHandler(Resolve(provider)));
        services.AddTransient(provider => new EconomicIdempotencyHandler(Resolve(provider)));
        services.AddTransient(provider => new EconomicRetryHandler(
            Resolve(provider).Retry,
            provider.GetService<TimeProvider>()));

        return services
            .AddHttpClient<EconomicClient>((provider, client) => client.BaseAddress = Resolve(provider).RestApiBaseAddress)

            // Constructed explicitly: EconomicClient offers a second constructor for callers not
            // using DI, and the default activator cannot choose between the two.
            .AddTypedClient((httpClient, _) => new EconomicClient(httpClient))

            // Order matters. Idempotency runs first so a key is assigned once and every retry
            // reuses it; retry sits inside it, and authentication applies to each attempt.
            .AddHttpMessageHandler<EconomicIdempotencyHandler>()
            .AddHttpMessageHandler<EconomicRetryHandler>()
            .AddHttpMessageHandler<EconomicAuthenticationHandler>();
    }

    private static EconomicOptions Resolve(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<EconomicOptions>>().Value;
}

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
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<EconomicOptions>>(new EconomicOptionsValidator());

        services.AddTransient(provider => new EconomicAuthenticationHandler(Resolve(provider)));
        services.AddTransient(provider => new EconomicIdempotencyHandler(Resolve(provider)));
        services.AddTransient(provider => new EconomicRetryHandler(
            Resolve(provider).Retry,
            provider.GetService<TimeProvider>()));

        return services
            .AddHttpClient<EconomicClient>((provider, client) => client.BaseAddress = Resolve(provider).RestApiBaseAddress)

            // Constructed explicitly: EconomicClient offers a second constructor for callers not
            // using DI, and the default activator cannot choose between the two. The OpenAPI
            // address is passed in because those services are addressed absolutely rather than
            // through the transport's base address, so configuring it was otherwise a no-op.
            .AddTypedClient((httpClient, provider) =>
                new EconomicClient(httpClient, Resolve(provider).OpenApiBaseAddress))

            // Order matters. Idempotency runs first so a key is assigned once and every retry
            // reuses it; retry sits inside it, and authentication applies to each attempt.
            .AddHttpMessageHandler<EconomicIdempotencyHandler>()
            .AddHttpMessageHandler<EconomicRetryHandler>()
            .AddHttpMessageHandler<EconomicAuthenticationHandler>();
    }

    private static EconomicOptions Resolve(IServiceProvider provider) =>
        provider.GetRequiredService<IOptions<EconomicOptions>>().Value;

    /// <summary>Reports <see cref="EconomicOptions.Validate"/>'s own message at startup.</summary>
    /// <remarks>
    /// A predicate-and-message <c>Validate</c> call cannot do this: it carries one fixed string, so
    /// every rejection read as a missing token — including a retry policy turned down by
    /// <see cref="EconomicRetryOptions.Validate"/>, which names a different property entirely.
    /// Registered as an instance rather than by type so nothing has to be activated by reflection.
    /// </remarks>
    private sealed class EconomicOptionsValidator : IValidateOptions<EconomicOptions>
    {
        public ValidateOptionsResult Validate(string? name, EconomicOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            try
            {
                options.Validate();
                return ValidateOptionsResult.Success;
            }
            catch (InvalidOperationException exception)
            {
                return ValidateOptionsResult.Fail(exception.Message);
            }
        }
    }
}

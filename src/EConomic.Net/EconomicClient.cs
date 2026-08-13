using EConomic.Authentication;

namespace EConomic;

/// <summary>
/// Entry point for the e-conomic APIs.
/// </summary>
/// <remarks>
/// <para>
/// Register with <c>AddEconomicClient</c> for the usual case, which wires up
/// <see cref="System.Net.Http.IHttpClientFactory"/>, authentication and the base address. The
/// two-argument constructor exists for callers not using dependency injection.
/// </para>
/// <para>
/// The instance is thread-safe and intended to be long-lived; queries built from it are
/// independent of each other.
/// </para>
/// </remarks>
public sealed partial class EconomicClient
{
    private readonly HttpClient _httpClient;

    /// <summary>The configured transport the generated resource properties build on.</summary>
    internal HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Creates a client over an <see cref="HttpClient"/> that is already configured with the base
    /// address and the authentication headers. This is the constructor dependency injection uses.
    /// </summary>
    /// <param name="httpClient">A configured client.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is <see langword="null"/>.</exception>
    public EconomicClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>
    /// Creates a client and configures the supplied <see cref="HttpClient"/> from the options.
    /// </summary>
    /// <param name="httpClient">The client to configure and use. Its base address and default
    /// headers are set, so it should not be shared with unrelated code.</param>
    /// <param name="options">Tokens and base addresses.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A required token is missing.</exception>
    public EconomicClient(HttpClient httpClient, EconomicOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        httpClient.BaseAddress ??= options.RestApiBaseAddress;

        // Without dependency injection there is no handler pipeline to hang authentication on, so
        // the tokens go on as default headers instead. Anything already set is left alone.
        if (!httpClient.DefaultRequestHeaders.Contains(EconomicAuthenticationHandler.AppSecretTokenHeader))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                EconomicAuthenticationHandler.AppSecretTokenHeader, options.AppSecretToken);
        }

        if (!httpClient.DefaultRequestHeaders.Contains(EconomicAuthenticationHandler.AgreementGrantTokenHeader))
        {
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                EconomicAuthenticationHandler.AgreementGrantTokenHeader, options.AgreementGrantToken);
        }

        _httpClient = httpClient;
    }

}

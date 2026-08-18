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
    private readonly Uri _openApiBaseAddress;

    // Built on first use and kept. Both are immutable wrappers over the transport, so a caller
    // writing client.Rest inside a loop was allocating one per iteration to reach the same
    // resources. Not built in the constructor: neither address is validated there, so an eagerly
    // composed surface would move a bad one's failure from first use to construction.
    private Rest.EconomicRestApi? _rest;
    private Open.EconomicOpenApi? _open;

    /// <summary>The configured transport the generated resource properties build on.</summary>
    internal HttpClient HttpClient => _httpClient;

    /// <summary>
    /// Creates a client over an <see cref="HttpClient"/> that is already configured with the base
    /// address and the authentication headers.
    /// </summary>
    /// <param name="httpClient">A configured client.</param>
    /// <remarks>
    /// The OpenAPI services are addressed absolutely rather than through the transport's base
    /// address, and this constructor is given no address to use, so they resolve against
    /// <see cref="EconomicOptions.DefaultOpenApiBaseAddress"/>. Anything else — a stub in tests, a
    /// proxy — has to name the address, which the options constructor and the registration both do.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is <see langword="null"/>.</exception>
    public EconomicClient(HttpClient httpClient)
        : this(httpClient, EconomicOptions.DefaultOpenApiBaseAddress)
    {
    }

    /// <summary>
    /// Creates a client over a transport dependency injection has already configured, pointing the
    /// OpenAPI services at the address the options named.
    /// </summary>
    /// <param name="httpClient">A configured client.</param>
    /// <param name="openApiBaseAddress">The root of the OpenAPI services.</param>
    /// <remarks>
    /// Internal because the registration is the only caller: it cannot use the options constructor
    /// below, which would put the tokens on as default headers and duplicate what the handler
    /// pipeline already does, and the single-argument one cannot carry the address at all.
    /// </remarks>
    internal EconomicClient(HttpClient httpClient, Uri openApiBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(openApiBaseAddress);

        _httpClient = httpClient;
        _openApiBaseAddress = openApiBaseAddress;
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
        _openApiBaseAddress = options.OpenApiBaseAddress;
    }

    /// <summary>
    /// The legacy REST API at <c>restapi.e-conomic.com</c>, which has the broader endpoint coverage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both surfaces hang off this client under their own name, because they are not
    /// interchangeable. Each publishes an entity called <c>Customer</c> and they disagree about it:
    /// here the server assigns the customer number and a reference is spelled
    /// <c>paymentTermsNumber</c>; on the OpenAPI services the caller supplies the number and it is
    /// <c>paymentTermId</c>. Naming the surface at the call site is what keeps them apart.
    /// </para>
    /// <para>
    /// They share everything below the models: one set of tokens, one transport, and — measured
    /// against a live agreement — one rate-limit budget, whose <c>X-RateLimiting</c> header moves
    /// together whichever host serves the request.
    /// </para>
    /// <para>
    /// Built once and kept. Two threads racing to be first may each build one, which is harmless:
    /// the surface holds nothing but the transport, so the two are interchangeable and whichever
    /// loses the race is collected.
    /// </para>
    /// </remarks>
    public Rest.EconomicRestApi Rest => _rest ??= new(_httpClient);

    /// <summary>
    /// The OpenAPI services at <c>apis.e-conomic.com</c>, versioned per service.
    /// </summary>
    /// <remarks>
    /// Reached over the same transport as <see cref="Rest"/> — the requests carry an absolute
    /// address, so one <see cref="HttpClient"/> serves both hosts and both share the agreement's
    /// single rate-limit budget. Built once and kept, on the same terms as <see cref="Rest"/>.
    /// </remarks>
    public Open.EconomicOpenApi Open => _open ??= new(_httpClient, _openApiBaseAddress);
}

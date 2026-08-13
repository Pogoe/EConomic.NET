namespace EConomic.Authentication;

/// <summary>
/// Attaches the e-conomic authentication headers to every outgoing request.
/// </summary>
/// <remarks>
/// Both the legacy REST API and the newer OpenAPI services accept the same two headers, so a single
/// handler serves both surfaces. Headers already present on a request are left untouched, which lets
/// a caller override the agreement per request.
/// </remarks>
public sealed class EconomicAuthenticationHandler : DelegatingHandler
{
    /// <summary>Name of the header carrying the app secret token.</summary>
    public const string AppSecretTokenHeader = "X-AppSecretToken";

    /// <summary>Name of the header carrying the agreement grant token.</summary>
    public const string AgreementGrantTokenHeader = "X-AgreementGrantToken";

    private readonly EconomicOptions _options;

    /// <summary>Creates a handler using the supplied options.</summary>
    /// <param name="options">Options carrying the two tokens. Validated immediately.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A required token is missing.</exception>
    public EconomicAuthenticationHandler(EconomicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.Contains(AppSecretTokenHeader))
        {
            request.Headers.TryAddWithoutValidation(AppSecretTokenHeader, _options.AppSecretToken);
        }

        if (!request.Headers.Contains(AgreementGrantTokenHeader))
        {
            request.Headers.TryAddWithoutValidation(AgreementGrantTokenHeader, _options.AgreementGrantToken);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

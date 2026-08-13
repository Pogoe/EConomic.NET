using EConomic.Authentication;

namespace EConomic.Http;

/// <summary>
/// Attaches an <c>Idempotency-Key</c> to requests that change state.
/// </summary>
/// <remarks>
/// <para>
/// e-conomic replays the original result for a repeated key rather than performing the operation
/// twice, marking the replay with <see cref="ResultFromCacheHeader"/>. That is what makes a write
/// safe to retry after a network failure, when the outcome of the first attempt is unknown.
/// </para>
/// <para>
/// The key is assigned once, before any retry, so every attempt of the same logical request
/// carries the same value. This handler must therefore sit <em>outside</em>
/// <see cref="EconomicRetryHandler"/>. Keys are not supported on <c>GET</c>.
/// </para>
/// </remarks>
public sealed class EconomicIdempotencyHandler : DelegatingHandler
{
    /// <summary>Name of the header carrying the caller-generated key.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Response header e-conomic sets when it replayed a previous result.</summary>
    public const string ResultFromCacheHeader = "X-ResultFromCache";

    private readonly EconomicOptions _options;

    /// <summary>Creates a handler using the supplied options.</summary>
    /// <param name="options">Options controlling whether keys are attached.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public EconomicIdempotencyHandler(EconomicOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Whether a request may safely carry an idempotency key.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <returns><see langword="true"/> for state-changing methods e-conomic accepts keys on.</returns>
    public static bool SupportsIdempotencyKey(HttpMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_options.SendIdempotencyKeys
            && SupportsIdempotencyKey(request.Method)
            && !request.Headers.Contains(IdempotencyKeyHeader))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, Guid.NewGuid().ToString("D"));
        }

        return base.SendAsync(request, cancellationToken);
    }
}

namespace EConomic.Authentication;

/// <summary>
/// Configuration for talking to the e-conomic APIs.
/// </summary>
/// <remarks>
/// Both e-conomic API surfaces authenticate the same way: an app token identifying your
/// integration and a grant token identifying the customer agreement you are acting on behalf of.
/// Neither token expires, and there is no refresh flow.
/// </remarks>
public sealed class EconomicOptions
{
    /// <summary>The token value that grants read-only access to e-conomic's public demo agreement.</summary>
    public const string DemoToken = "demo";

    /// <summary>Default base address of the legacy REST API.</summary>
    public static readonly Uri DefaultRestApiBaseAddress = new("https://restapi.e-conomic.com/");

    /// <summary>Default base address of the versioned OpenAPI services.</summary>
    public static readonly Uri DefaultOpenApiBaseAddress = new("https://apis.e-conomic.com/");

    /// <summary>
    /// Secret token identifying your integration, sent as the
    /// <c>X-AppSecretToken</c> header. Never log this value.
    /// </summary>
    public string AppSecretToken { get; set; } = string.Empty;

    /// <summary>
    /// Token identifying the customer agreement being accessed, sent as the
    /// <c>X-AgreementGrantToken</c> header. Never log this value.
    /// </summary>
    public string AgreementGrantToken { get; set; } = string.Empty;

    /// <summary>Base address of the legacy REST API. Defaults to <see cref="DefaultRestApiBaseAddress"/>.</summary>
    public Uri RestApiBaseAddress { get; set; } = DefaultRestApiBaseAddress;

    /// <summary>Base address of the OpenAPI services. Defaults to <see cref="DefaultOpenApiBaseAddress"/>.</summary>
    public Uri OpenApiBaseAddress { get; set; } = DefaultOpenApiBaseAddress;

    /// <summary>
    /// Whether to attach a generated <c>Idempotency-Key</c> header to non-GET requests that do not
    /// already carry one, so a retried write is not applied twice. Defaults to <see langword="true"/>.
    /// </summary>
    public bool SendIdempotencyKeys { get; set; } = true;

    /// <summary>
    /// Creates options pointing at e-conomic's read-only demo agreement. Intended for samples and tests.
    /// </summary>
    /// <returns>Options with both tokens set to <see cref="DemoToken"/>.</returns>
    public static EconomicOptions Demo() => new()
    {
        AppSecretToken = DemoToken,
        AgreementGrantToken = DemoToken,
    };

    /// <summary>Throws if the options are not usable.</summary>
    /// <exception cref="InvalidOperationException">A required token is missing.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppSecretToken))
        {
            throw new InvalidOperationException(
                $"{nameof(AppSecretToken)} is required. Create one at https://www.e-conomic.com/developer/connect.");
        }

        if (string.IsNullOrWhiteSpace(AgreementGrantToken))
        {
            throw new InvalidOperationException(
                $"{nameof(AgreementGrantToken)} is required. Create one at https://www.e-conomic.com/developer/connect.");
        }
    }

    /// <summary>Returns a description that deliberately omits both tokens.</summary>
    /// <returns>A redacted description safe to write to logs.</returns>
    public override string ToString() =>
        $"EconomicOptions {{ RestApiBaseAddress = {RestApiBaseAddress}, OpenApiBaseAddress = {OpenApiBaseAddress}, tokens redacted }}";
}

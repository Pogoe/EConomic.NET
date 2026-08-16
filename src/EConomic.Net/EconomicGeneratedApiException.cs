using System.Diagnostics.CodeAnalysis;

namespace EConomic;

/// <summary>
/// The failure type the generated clients throw.
/// </summary>
/// <remarks>
/// Hand-written rather than generated so that it can be <see langword="internal"/>: NSwag has no
/// setting for the access modifier of its exception classes, and the layering rule is that nothing
/// generated reaches the public surface. The shape must match what NSwag emits — it constructs this
/// type by name — so change it only alongside a regeneration.
/// <para>
/// It sits in the root namespace rather than beside either set of generated clients because both
/// throw it. C# resolves the unqualified name outwards, so <c>EConomic.Rest.Generated</c> and
/// <c>EConomic.Open.Generated</c> each find this one type without either referring to the other —
/// the surfaces stay separate, and there is still only one of these.
/// </para>
/// <para>
/// This never escapes the library. The facade catches it and rethrows
/// <see cref="Exceptions.EconomicApiException"/>, which carries the parsed error body.
/// </para>
/// </remarks>
[SuppressMessage("Minor Code Smell", "S3871:Exception types should be public",
    Justification = "Internal on purpose. This is the layering rule the facade exists to enforce: nothing "
        + "generated reaches the public surface, and this type never escapes the library — the facade "
        + "catches it and rethrows the public EconomicApiException.")]
[SuppressMessage("Style", "IDE0290:Use primary constructor",
    Justification = "The shape has to match what NSwag constructs by name, so this file is only changed "
        + "alongside a regeneration; restructuring it for style is not worth that coupling.")]
internal class EconomicGeneratedApiException : Exception
{
    /// <summary>How much of the response body the message quotes before truncating.</summary>
    private const int MessageBodyLimit = 512;

    /// <summary>Creates an exception describing an unsuccessful response.</summary>
    public EconomicGeneratedApiException(
        string message,
        int statusCode,
        string? response,
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        Exception? innerException)
        : base(
            message + "\n\nStatus: " + statusCode + "\nResponse: \n" + Truncate(response),
            innerException)
    {
        StatusCode = statusCode;
        Response = response;
        Headers = headers;
    }

    /// <summary>The HTTP status code returned.</summary>
    public int StatusCode { get; }

    /// <summary>The raw response body, truncated in the message but complete here.</summary>
    public string? Response { get; }

    /// <summary>The response headers.</summary>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers { get; }

    /// <inheritdoc />
    public override string ToString() =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "HTTP Response: \n\n{0}\n\n{1}", Response, base.ToString());

    /// <summary>The leading <see cref="MessageBodyLimit"/> characters of the body, for the message.</summary>
    private static string Truncate(string? response) =>
        response is null ? "(null)" : response[..Math.Min(response.Length, MessageBodyLimit)];
}

/// <summary>The failure type the generated clients throw when a typed error body was parsed.</summary>
/// <typeparam name="TResult">Type of the parsed error body.</typeparam>
[SuppressMessage("Style", "IDE0290:Use primary constructor",
    Justification = "The shape has to match what NSwag constructs by name, so this file is only changed "
        + "alongside a regeneration; restructuring it for style is not worth that coupling.")]
internal sealed class EconomicGeneratedApiException<TResult> : EconomicGeneratedApiException
{
    /// <summary>Creates an exception carrying a parsed error body.</summary>
    public EconomicGeneratedApiException(
        string message,
        int statusCode,
        string? response,
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        TResult result,
        Exception? innerException)
        : base(message, statusCode, response, headers, innerException)
        => Result = result;

    /// <summary>The parsed error body.</summary>
    public TResult Result { get; }
}

namespace EConomic.Rest.Generated;

/// <summary>
/// The failure type the generated clients throw.
/// </summary>
/// <remarks>
/// Hand-written rather than generated so that it can be <see langword="internal"/>: NSwag has no
/// setting for the access modifier of its exception classes, and the layering rule is that nothing
/// generated reaches the public surface. The shape must match what NSwag emits — it constructs this
/// type by name — so change it only alongside a regeneration.
/// <para>
/// This never escapes the library. The facade catches it and rethrows
/// <see cref="Exceptions.EconomicApiException"/>, which carries the parsed error body.
/// </para>
/// </remarks>
internal class EconomicGeneratedApiException : Exception
{
    /// <summary>Creates an exception describing an unsuccessful response.</summary>
    public EconomicGeneratedApiException(
        string message,
        int statusCode,
        string? response,
        IReadOnlyDictionary<string, IEnumerable<string>> headers,
        Exception? innerException)
        : base(
            message + "\n\nStatus: " + statusCode + "\nResponse: \n"
            + (response is null ? "(null)" : response[..(response.Length >= 512 ? 512 : response.Length)]),
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
}

/// <summary>The failure type the generated clients throw when a typed error body was parsed.</summary>
/// <typeparam name="TResult">Type of the parsed error body.</typeparam>
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

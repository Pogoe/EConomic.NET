using System.Net.Http.Headers;

namespace EConomic.Http;

/// <summary>
/// Reads the response headers this library acts on, from both of the shapes it receives them in.
/// </summary>
/// <remarks>
/// A live response carries <see cref="HttpResponseHeaders"/>, which matches names without regard to
/// case on its own. The generated clients hand the facade a plain dictionary instead, and by then
/// the response is gone — so the same header has to be found twice, two different ways. Keeping
/// both here is what stops the second one quietly diverging from the first.
/// </remarks>
internal static class HeaderReading
{
    /// <summary>Reads one header out of the dictionary a generated client captured.</summary>
    /// <param name="headers">The captured headers.</param>
    /// <param name="name">The header name.</param>
    /// <returns>The first value, or <see langword="null"/> if the header is absent.</returns>
    /// <remarks>
    /// Matched without regard to case. The generated clients build an ordinary
    /// <see cref="Dictionary{TKey, TValue}"/> keyed by whatever casing arrived, and HTTP field names
    /// are case-insensitive — HTTP/2 lowercases them on the wire — so an exact miss is not an absent
    /// header. Getting this wrong reports nothing rather than failing, which is the kind of mistake
    /// nothing downstream can notice.
    /// </remarks>
    public static string? Value(IReadOnlyDictionary<string, IEnumerable<string>> headers, string name)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // The exact hit is the common case and costs one lookup; the scan is the fallback.
        if (headers.TryGetValue(name, out var values))
        {
            return values?.FirstOrDefault();
        }

        foreach (var (key, candidates) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidates?.FirstOrDefault();
            }
        }

        return null;
    }

    /// <summary>How long a <c>Retry-After</c> header asks the caller to wait.</summary>
    /// <param name="headers">The response headers.</param>
    /// <param name="now">The current time, so the date form can be turned into a delay.</param>
    /// <returns>The delay, or <see langword="null"/> if the server named none.</returns>
    /// <remarks>
    /// <para>
    /// RFC 9110 gives <c>Retry-After</c> two forms: delta-seconds, and an HTTP-date. .NET parses
    /// them into different properties of the same value — <c>Delta</c> and <c>Date</c> — and reading
    /// only <c>Delta</c> silently ignores the date form, which is how this was wrong: the header was
    /// there, the server had said exactly how long to wait, and the retry fell back to its own
    /// backoff curve as though nothing had been sent.
    /// </para>
    /// <para>
    /// A date already in the past yields <see cref="TimeSpan.Zero"/> rather than a negative delay,
    /// so a caller can subtract or display it without a special case.
    /// </para>
    /// </remarks>
    public static TimeSpan? RetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var remaining = date - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }
}

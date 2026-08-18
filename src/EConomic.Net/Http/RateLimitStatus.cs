using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EConomic.Http;

/// <summary>
/// The rate-limiting budget e-conomic reports on every response.
/// </summary>
/// <remarks>
/// <para>
/// e-conomic does not count requests. It charges each call a number of tokens against a bucket that
/// refills over a fixed window, so an expensive query costs more than a cheap one. Every response
/// carries <see cref="CallCostHeader"/> (what this call cost) and <see cref="RateLimitingHeader"/>
/// (the bucket state), for example:
/// </para>
/// <code>X-CallCost: 3
/// X-RateLimiting: token-limit-10000-per-60-seconds: 147/10000</code>
/// <para>
/// The first number is the amount already <see cref="Used"/> within the window — it climbs as calls
/// are made, and exhausting the bucket produces <c>429 Too Many Requests</c>.
/// </para>
/// </remarks>
public sealed partial class RateLimitStatus
{
    /// <summary>Name of the header reporting what a single call cost, in tokens.</summary>
    public const string CallCostHeader = "X-CallCost";

    /// <summary>Name of the header reporting the current bucket state.</summary>
    public const string RateLimitingHeader = "X-RateLimiting";

    private RateLimitStatus(int limit, TimeSpan window, int used, int? callCost)
    {
        Limit = limit;
        Window = window;
        Used = used;
        CallCost = callCost;
    }

    /// <summary>Total tokens available per window.</summary>
    public int Limit { get; }

    /// <summary>Length of the window the limit applies to.</summary>
    public TimeSpan Window { get; }

    /// <summary>Tokens consumed so far within the current window.</summary>
    public int Used { get; }

    /// <summary>Tokens still available in the current window; never negative.</summary>
    public int Remaining => Math.Max(0, Limit - Used);

    /// <summary>What the call that produced this status cost, if the response reported it.</summary>
    public int? CallCost { get; }

    /// <summary>Fraction of the budget consumed, from 0.0 to 1.0.</summary>
    public double UsedFraction => Limit <= 0 ? 0d : Math.Min(1d, (double)Used / Limit);

    /// <summary>Reads the rate-limit status from a response's headers.</summary>
    /// <param name="response">The response to inspect.</param>
    /// <returns>The parsed status, or <see langword="null"/> if the response carried no usable headers.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
    public static RateLimitStatus? FromResponse(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues(RateLimitingHeader, out var rateLimitValues))
        {
            return null;
        }

        int? callCost = null;
        if (response.Headers.TryGetValues(CallCostHeader, out var costValues)
            && int.TryParse(costValues.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cost))
        {
            callCost = cost;
        }

        return TryParse(rateLimitValues.FirstOrDefault(), callCost, out var status) ? status : null;
    }

    /// <summary>Reads the rate-limit status from the headers a generated client captured.</summary>
    /// <param name="headers">The response headers, as the generated failure type carries them.</param>
    /// <returns>The parsed status, or <see langword="null"/> if the headers carried none.</returns>
    /// <remarks>
    /// The generated clients hand the facade a dictionary rather than an
    /// <see cref="HttpResponseMessage"/>, and by the time a failure is translated the response is
    /// gone. Without this, every exception raised through a generated call reported no budget at
    /// all — which was all of them but the hand-written <c>DELETE</c>.
    /// </remarks>
    internal static RateLimitStatus? FromHeaders(IReadOnlyDictionary<string, IEnumerable<string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var rateLimiting = HeaderReading.Value(headers, RateLimitingHeader);
        if (rateLimiting is null)
        {
            return null;
        }

        int? callCost = null;
        if (int.TryParse(
            HeaderReading.Value(headers, CallCostHeader),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var cost))
        {
            callCost = cost;
        }

        return TryParse(rateLimiting, callCost, out var status) ? status : null;
    }

    /// <summary>Parses an <c>X-RateLimiting</c> header value.</summary>
    /// <param name="headerValue">The raw header value, e.g. <c>token-limit-10000-per-60-seconds: 147/10000</c>.</param>
    /// <param name="status">The parsed status when parsing succeeds.</param>
    /// <returns><see langword="true"/> if the value was understood.</returns>
    public static bool TryParse(string? headerValue, [NotNullWhen(true)] out RateLimitStatus? status) =>
        TryParse(headerValue, callCost: null, out status);

    private static bool TryParse(string? headerValue, int? callCost, [NotNullWhen(true)] out RateLimitStatus? status)
    {
        status = null;

        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        var match = HeaderPattern().Match(headerValue);
        if (!match.Success)
        {
            return false;
        }

        var invariant = CultureInfo.InvariantCulture;
        if (!int.TryParse(match.Groups["limit"].ValueSpan, NumberStyles.Integer, invariant, out var limit)
            || !int.TryParse(match.Groups["seconds"].ValueSpan, NumberStyles.Integer, invariant, out var seconds)
            || !int.TryParse(match.Groups["used"].ValueSpan, NumberStyles.Integer, invariant, out var used))
        {
            return false;
        }

        status = new RateLimitStatus(limit, TimeSpan.FromSeconds(seconds), used, callCost);
        return true;
    }

    /// <summary>Returns a redacted, human-readable description of the budget.</summary>
    /// <returns>A string such as <c>147/10000 tokens used per 60s</c>.</returns>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Used}/{Limit} tokens used per {Window.TotalSeconds:0}s");

    // The trailing total restates the limit already read from the prefix, so it is matched but not
    // captured — naming a group nothing reads only costs a capture per parse.
    [GeneratedRegex(
        @"token-limit-(?<limit>\d+)-per-(?<seconds>\d+)-seconds\s*:\s*(?<used>\d+)\s*/\s*\d+",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex HeaderPattern();
}

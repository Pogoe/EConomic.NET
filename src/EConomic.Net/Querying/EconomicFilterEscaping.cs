using System.Text;

namespace EConomic.Querying;

/// <summary>
/// Escaping for values placed into an e-conomic <c>filter=</c> expression.
/// </summary>
/// <remarks>
/// The table below is not invented: the legacy REST API publishes it in the body of a
/// filter-parse failure, alongside the operators it accepts. Values are always escaped by the
/// query builder, never left to the caller.
/// <para>
/// Note that <c>*</c> is the wildcard character, so a literal asterisk in a value has to be
/// escaped as <c>$*</c> or it would silently widen the match.
/// </para>
/// </remarks>
public static class EconomicFilterEscaping
{
    /// <summary>The wildcard character in a <c>$like:</c> expression.</summary>
    public const char Wildcard = '*';

    /// <summary>
    /// The token matching properties that are absent. It is a <em>value</em>, so it follows an
    /// operator — <c>ean$eq:$null:</c>. Written on its own, <c>ean$null:</c> is a syntax error.
    /// </summary>
    public const string NullValue = "$null:";

    /// <summary>Escapes a value for use in a filter expression.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>The escaped value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Escape(value, escapeWildcards: true);
    }

    /// <summary>
    /// Escapes a value that may legitimately contain wildcards, as in a <c>$like:</c> pattern.
    /// </summary>
    /// <param name="pattern">The pattern, in which <c>*</c> keeps its wildcard meaning.</param>
    /// <returns>The escaped pattern.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is <see langword="null"/>.</exception>
    public static string EscapePattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return Escape(pattern, escapeWildcards: false);
    }

    private static string Escape(string value, bool escapeWildcards)
    {
        var builder = new StringBuilder(value.Length);

        foreach (char character in value)
        {
            switch (character)
            {
                case '$':
                case '(':
                case ')':
                case '[':
                case ']':
                case ',':
                    builder.Append('$').Append(character);
                    break;

                case Wildcard when escapeWildcards:
                    builder.Append('$').Append(Wildcard);
                    break;

                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}

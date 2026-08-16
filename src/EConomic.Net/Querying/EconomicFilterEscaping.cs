using System.Text;

namespace EConomic.Querying;

/// <summary>
/// Escaping for values placed into an e-conomic <c>filter=</c> expression.
/// </summary>
/// <remarks>
/// Most of the table is not invented: the legacy REST API publishes it in the body of a
/// filter-parse failure, alongside the operators it accepts. Values are always escaped by the
/// query builder, never left to the caller.
/// <para>
/// Note that <c>*</c> is the wildcard character, so a literal asterisk in a value has to be
/// escaped as <c>$*</c> or it would silently widen the match.
/// </para>
/// <para>
/// <b>Square brackets are the one place the published table is wrong, and it fails silently.</b>
/// The server lists <c>$[</c> and <c>$]</c> alongside the rest, but does not honour them: a record
/// named <c>ZZ[X]tail</c> is returned by <c>name$eq:ZZ[X]tail</c> and by nothing else — the escaped
/// <c>name$eq:ZZ$[X$]tail</c> parses cleanly and matches no rows. Verified against a live agreement
/// by creating one customer per character in the table and filtering for each. So a bracket is left
/// alone under <see cref="Escape(string)"/>.
/// </para>
/// <para>
/// Under <c>$like:</c> a bracket is not literal at all: <c>[…]</c> is a SQL character class.
/// <c>name$like:ZZcc[XY]tail</c> returns both <c>ZZccXtail</c> and <c>ZZccYtail</c>, and neither
/// <c>$[</c> nor a bare bracket matches one. The idiom that does work is SQL's own — a literal
/// <c>[</c> is written <c>[[]</c> — which is what <see cref="EscapePattern"/> emits. A <c>]</c>
/// outside a class is already literal and needs nothing.
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

        foreach (var character in value)
        {
            switch (character)
            {
                case '$':
                case '(':
                case ')':
                case ',':
                    builder.Append('$').Append(character);
                    break;

                case Wildcard when escapeWildcards:
                    builder.Append('$').Append(Wildcard);
                    break;

                // Only meaningful in a `$like:` pattern, where it opens a character class. In a
                // comparison it is already literal, and `$[` would match nothing — see the remarks.
                case '[' when !escapeWildcards:
                    builder.Append("[[]");
                    break;

                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}

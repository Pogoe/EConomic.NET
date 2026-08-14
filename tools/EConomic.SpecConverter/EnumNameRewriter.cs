using System.Text.RegularExpressions;

namespace EConomic.SpecConverter;

/// <summary>
/// Renames generated enum members to the values e-conomic actually sends.
/// </summary>
/// <remarks>
/// <para>
/// NSwag names an enum member <c>Net</c> for the value <c>"net"</c> and records the original on a
/// <c>[EnumMember]</c> attribute, which <c>System.Text.Json</c> does not read. Reading survived
/// that because enum deserialization is case-insensitive; writing did not. Sending
/// <c>"paymentTermsType": "Net"</c> is rejected with <c>Value "Net" is not defined in enum</c>.
/// </para>
/// <para>
/// The obvious fix — a <c>JsonStringEnumConverter</c> with a camel-case policy — is reflection
/// based and would break the trimming and AOT guarantees. Renaming the member instead keeps the
/// source-generated converter and costs nothing at run time. The enums are internal, so the names
/// are invisible to consumers.
/// </para>
/// <para>
/// Only members whose value is already a valid identifier are renamed; anything else is reported
/// and left alone, because a mangled name would be worse than the original.
/// </para>
/// </remarks>
public static partial class EnumNameRewriter
{
    /// <summary>C# keywords that need an <c>@</c> prefix to be used as identifiers.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    /// <summary>Rewrites the enum member names in generated source.</summary>
    /// <param name="generatedSource">Contents of the NSwag-generated file.</param>
    /// <param name="skipped">Collects values that could not be used as identifiers.</param>
    /// <returns>The rewritten source and how many members changed.</returns>
    public static (string Source, int Renamed) Rewrite(string generatedSource, IList<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(generatedSource);
        ArgumentNullException.ThrowIfNull(skipped);

        var renamed = 0;

        var result = MemberPattern().Replace(generatedSource, match =>
        {
            var value = match.Groups["value"].Value;
            var member = match.Groups["member"].Value;

            if (!IsIdentifier(value))
            {
                skipped.Add($"{member} = \"{value}\" (not a valid identifier)");
                return match.Value;
            }

            var name = Keywords.Contains(value) ? "@" + value : value;
            if (name == member)
            {
                return match.Value;
            }

            renamed++;
            return match.Value.Replace($"{member} =", $"{name} =", StringComparison.Ordinal);
        });

        return (result, renamed);
    }

    private static bool IsIdentifier(string value) =>
        value.Length > 0
        && (char.IsLetter(value[0]) || value[0] == '_')
        && value.All(c => char.IsLetterOrDigit(c) || c == '_');

    [GeneratedRegex(
        """\[System\.Runtime\.Serialization\.EnumMember\(Value = @"(?<value>[^"]*)"\)\]\s*\r?\n\s*(?<member>@?\w+) = """,
        RegexOptions.ExplicitCapture)]
    private static partial Regex MemberPattern();
}

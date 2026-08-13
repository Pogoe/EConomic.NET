using System.Text;

namespace EConomic.SpecConverter;

/// <summary>
/// One legacy schema file, resolved to the HTTP endpoint it describes.
/// </summary>
/// <param name="SourceFile">File name the schema came from, kept for traceability.</param>
/// <param name="Resource">Top-level resource, used to group endpoints into documents.</param>
/// <param name="Path">OpenAPI path template, e.g. <c>/customers/{customerNumber}/contacts</c>.</param>
/// <param name="Method">Lower-case HTTP method.</param>
/// <param name="Parameters">Path parameter names, in the order they appear.</param>
public sealed record LegacyEndpoint(
    string SourceFile,
    string Resource,
    string Path,
    string Method,
    IReadOnlyList<string> Parameters);

/// <summary>
/// Resolves a legacy schema file name such as
/// <c>customers.customerNumber.contacts.contactNumber.get.schema.json</c> into a path template.
/// </summary>
/// <remarks>
/// Casing cannot decide which segments are parameters: <c>manualCustomerInvoice</c> and
/// <c>financeVoucher</c> are literal template names despite being camelCase, while <c>id</c>,
/// <c>code</c> and <c>customergroupnumber</c> are parameters despite being lower-case. So the
/// parameter names are enumerated explicitly and anything unrecognised is treated as a literal.
/// <see cref="UnknownCamelCaseSegments"/> reports segments that look like parameters but are not
/// listed, so a spec refresh that adds one is caught rather than silently mis-routed.
/// </remarks>
public static class EndpointResolver
{
    private const string SchemaSuffix = ".schema.json";

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "get", "post", "put", "delete", "patch",
    };

    /// <summary>Segments that are path parameters rather than literal path text.</summary>
    public static readonly IReadOnlySet<string> ParameterSegments = new HashSet<string>(StringComparer.Ordinal)
    {
        "accountNumber", "accountingPeriod", "accountingYear", "accountingYearPeriod",
        "bookWithNumber", "bookedInvoiceNumber", "code", "contactNumber", "currencyCode",
        "customerGroupNumber", "customergroupnumber", "customerNo", "customerNumber",
        "deliveryLocationNumber", "departmentNumber", "departmentalDistributionNumber",
        "draftInvoiceNumber", "employeeNumber", "entryNumber", "id", "journalNumber",
        "layoutNumber", "orderNumber", "paymentTermsNumber", "paymentTypeNumber",
        "productGroupNumber", "productNumber", "quoteNumber", "roleNumber", "supplierNumber",
        "unitNumber", "vatZoneNumber", "voucherNumber",
    };

    /// <summary>
    /// camelCase segments that are literal path text. Without these the casing heuristic would
    /// wrongly turn journal template names into parameters.
    /// </summary>
    public static readonly IReadOnlySet<string> LiteralOverrides = new HashSet<string>(StringComparer.Ordinal)
    {
        "manualCustomerInvoice", "financeVoucher",
    };

    /// <summary>Resolves a file name to the endpoint it describes.</summary>
    /// <param name="fileName">File name, with or without directories.</param>
    /// <returns>The resolved endpoint.</returns>
    /// <exception cref="ArgumentException">The name is not a legacy schema file name.</exception>
    public static LegacyEndpoint Resolve(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var name = System.IO.Path.GetFileName(fileName);
        if (!name.EndsWith(SchemaSuffix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' does not end in '{SchemaSuffix}'.", nameof(fileName));
        }

        var segments = name[..^SchemaSuffix.Length].Split('.');
        if (segments.Length < 2)
        {
            throw new ArgumentException($"'{name}' has no method segment.", nameof(fileName));
        }

        var method = segments[^1];
        if (!Methods.Contains(method))
        {
            throw new ArgumentException($"'{name}' ends in unknown HTTP method '{method}'.", nameof(fileName));
        }

        var pathSegments = segments[..^1];
        var parameters = new List<string>();
        var path = new StringBuilder();

        foreach (var segment in pathSegments)
        {
            path.Append('/').Append(RenderSegment(segment, parameters));
        }

        return new LegacyEndpoint(name, pathSegments[0], path.ToString(), method, parameters);
    }

    /// <summary>
    /// Segments across the given file names that look like parameters but are not declared as
    /// either a parameter or a literal. A non-empty result means the tables need updating.
    /// </summary>
    /// <param name="fileNames">The schema file names to inspect.</param>
    /// <returns>The unrecognised segments, sorted.</returns>
    public static IReadOnlyList<string> UnknownCamelCaseSegments(IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);

        var unknown = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var fileName in fileNames)
        {
            var name = System.IO.Path.GetFileName(fileName);
            if (!name.EndsWith(SchemaSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var segments = name[..^SchemaSuffix.Length].Split('.');
            foreach (var segment in segments[..^1])
            {
                foreach (var part in segment.Split('-'))
                {
                    if (LooksLikeParameter(part)
                        && !ParameterSegments.Contains(part)
                        && !LiteralOverrides.Contains(part))
                    {
                        unknown.Add(part);
                    }
                }
            }
        }

        return [.. unknown];
    }

    private static string RenderSegment(string segment, List<string> parameters)
    {
        if (ParameterSegments.Contains(segment))
        {
            parameters.Add(segment);
            return $"{{{segment}}}";
        }

        // A composite key such as `accountingYear-voucherNumber` addresses one resource with two
        // values joined by a hyphen. Only treat a hyphenated segment as composite when every part
        // is a known parameter, so literals like `accounting-years` are left alone.
        if (segment.Contains('-', StringComparison.Ordinal))
        {
            var parts = segment.Split('-');
            if (parts.All(ParameterSegments.Contains))
            {
                parameters.AddRange(parts);
                return string.Join("-", parts.Select(p => $"{{{p}}}"));
            }
        }

        return segment;
    }

    private static bool LooksLikeParameter(string segment)
    {
        for (var i = 1; i < segment.Length; i++)
        {
            if (char.IsUpper(segment[i]) && char.IsLower(segment[i - 1]))
            {
                return true;
            }
        }

        return false;
    }
}

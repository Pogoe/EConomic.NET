using System.Reflection;
using System.Text.Json;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Checks that the pinned service versions match the specifications they were generated from.
/// </summary>
/// <remarks>
/// Each OpenAPI service carries its version in the URL, and the client composes that address from a
/// constant. Nothing else connects the two: a specification refresh that moves
/// <c>customersapi/v3.1.0</c> to <c>v4.0.0</c> would leave the client addressing a version the
/// generated code no longer matches, and the mismatch would only appear as a puzzling failure at
/// run time. This turns it into a failing build.
/// </remarks>
public class OpenServiceVersionTests
{
    [Fact]
    public void Every_pinned_service_version_matches_its_specification()
    {
        var services = typeof(EconomicClient).Assembly
            .GetType("EConomic.Open.EconomicOpenApi+Services", throwOnError: true)!
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral)
            .ToList();

        Assert.NotEmpty(services);

        var published = Published();

        foreach (var service in services)
        {
            var pinned = ((string)service.GetRawConstantValue()!).TrimEnd('/');

            // Matched on the address rather than on a file name. e-conomic names one document
            // webhooks-api, which is not an identifier and so cannot be the constant's name, and a
            // second table mapping one to the other would be one more thing to keep in step. This
            // asks the stronger question anyway: is there a published specification that serves
            // exactly this address?
            var matches = published.Where(p => p.Server.EndsWith(pinned, StringComparison.Ordinal)).ToList();

            Assert.True(
                matches.Count == 1,
                $"{service.Name} is pinned to '{pinned}', which {matches.Count} published "
                + $"specifications serve. Addresses found: {string.Join(", ", published.Select(p => p.Server))}");
        }
    }

    /// <summary>The address each published specification serves.</summary>
    private static List<(string File, string Server)> Published() =>
        [.. Directory.GetFiles(SpecificationDirectory(), "openapi-*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(file =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                return (
                    Path.GetFileName(file),
                    document.RootElement.GetProperty("servers")[0].GetProperty("url").GetString()!.TrimEnd('/'));
            })];

    /// <summary>The specifications, found by walking up from the test binary to the repository.</summary>
    private static string SpecificationDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "specs", "openapi");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find specs/openapi above the test binary.");
    }
}

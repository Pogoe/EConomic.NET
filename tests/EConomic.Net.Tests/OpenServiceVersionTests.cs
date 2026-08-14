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

        foreach (var service in services)
        {
            var pinned = (string)service.GetRawConstantValue()!;
            var specification = Path.Combine(SpecificationDirectory(), $"openapi-{service.Name}.json");

            Assert.True(File.Exists(specification), $"No specification at {specification}.");

            using var document = JsonDocument.Parse(File.ReadAllText(specification));
            var server = document.RootElement.GetProperty("servers")[0].GetProperty("url").GetString();

            // The pinned value is the path segment: everything after the host, with its trailing
            // slash, e.g. "customersapi/v3.1.0/".
            Assert.EndsWith(pinned.TrimEnd('/'), server?.TrimEnd('/') ?? string.Empty, StringComparison.Ordinal);
        }
    }

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

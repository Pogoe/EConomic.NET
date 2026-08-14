using System.Reflection;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Fetches a page from every resource on the client.
/// </summary>
/// <remarks>
/// <para>
/// The other tests cover a handful of resources in depth. This one covers all of them in the
/// shallowest way that still means something: each has its own generated client class, method and
/// collection envelope, and getting any of those wrong produces a resource that throws the moment
/// it is used. That is precisely the failure a generator repeats across every resource it emits.
/// </para>
/// <para>
/// The list comes from reflection rather than from a hand-written one, so a resource added later is
/// covered without anyone remembering to add it here. An empty page is a pass — the assertion is
/// that the call round-trips and maps, not that the agreement holds data.
/// </para>
/// </remarks>
public class EveryResourceTests
{
    [Fact]
    public async Task Every_resource_returns_a_page()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        var probed = new List<string>();
        var failures = new List<string>();

        foreach (var (surface, property) in Collections(client))
        {
            var resource = property.GetValue(surface)!;
            var getPage = resource.GetType().GetMethod(
                "GetPageAsync",
                [typeof(int), typeof(CancellationToken)])!;

            try
            {
                await (Task)getPage.Invoke(resource, [0, token])!;
                probed.Add(property.Name);
            }
            catch (TargetInvocationException exception)
            {
                failures.Add($"{property.Name}: {exception.InnerException?.Message}");
            }
        }

        Assert.Empty(failures);

        // A guard against the reflection silently matching nothing, which would make this pass
        // while testing zero resources.
        Assert.True(probed.Count >= 30, $"Expected to probe every resource; only found {probed.Count}.");
    }

    /// <summary>
    /// Every property that exposes a page — a query or a resource, alike — on every API surface.
    /// </summary>
    /// <remarks>
    /// Walking the surfaces rather than the client means the OpenAPI services are swept the moment
    /// they hang off <c>client.Open</c>, with nothing here to remember to change.
    /// </remarks>
    private static IEnumerable<(object Surface, PropertyInfo Property)> Collections(EconomicClient client) =>
        typeof(EconomicClient)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.Name.EndsWith("Api", StringComparison.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .SelectMany(surface => surface.PropertyType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetMethod is not null
                    && p.GetIndexParameters().Length == 0
                    && p.PropertyType.GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)]) is not null)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => (Surface: surface.GetValue(client)!, Property: p)));
}

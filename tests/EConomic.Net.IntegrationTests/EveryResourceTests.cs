using System.Reflection;
using EConomic.Exceptions;
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
        var elsewhere = new List<string>();
        List<(object Surface, PropertyInfo Property)>? onDemo = null;

        foreach (var (surface, property) in Collections(client))
        {
            var module = await PageAsync(surface, property, token);
            if (module is null)
            {
                probed.Add(property.Name);
                continue;
            }

            if (module.StartsWith("failed: ", StringComparison.Ordinal))
            {
                failures.Add($"{property.Name}: {module["failed: ".Length..]}");
                continue;
            }

            // The agreement has not bought the module this one belongs to. That is a fact about the
            // agreement, so the same resource is tried on the demo agreement rather than dropped —
            // a resource nothing ever called is exactly what this test exists to prevent.
            onDemo ??= [.. Collections(TestClients.CreateDemo())];
            var fallback = onDemo.FirstOrDefault(c =>
                c.Surface.GetType() == surface.GetType() && c.Property.Name == property.Name);

            var refused = fallback.Property is null
                ? "it is not exposed there"
                : await PageAsync(fallback.Surface, fallback.Property, token);

            if (refused is not null)
            {
                failures.Add($"{property.Name}: needs the {module} module, and on demo, {refused}");
                continue;
            }

            probed.Add(property.Name);
            elsewhere.Add($"{property.Name} ({module})");
        }

        // Spelled out rather than Assert.Empty, which abbreviates the collection and hides the
        // server's own explanation — the only part of the message worth reading.
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // Only the projects service is sold separately today. Anything else landing here means the
        // configured agreement lost a module, which would otherwise quietly move a whole service
        // onto the demo agreement's data while the run stayed green.
        var unexpected = elsewhere
            .Where(u => !u.EndsWith("(Project)", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Modules missing beyond the projects service: {string.Join(", ", unexpected)}.");

        // A guard against the reflection silently matching nothing, which would make this pass
        // while testing zero resources.
        Assert.True(probed.Count >= 30, $"Expected to probe every resource; only found {probed.Count}.");
    }

    /// <summary>Fetches the first page of one resource.</summary>
    /// <returns>
    /// <see langword="null"/> when the page came back; the name of the module the agreement lacks
    /// when that is why it did not; or the failure prefixed with <c>failed:</c> for anything else.
    /// </returns>
    private static async Task<string?> PageAsync(object surface, PropertyInfo property, CancellationToken token)
    {
        var resource = property.GetValue(surface)!;
        var getPage = resource.GetType().GetMethod(
            "GetPageAsync",
            [typeof(int), typeof(CancellationToken)])!;

        try
        {
            // Invoke returns the Task the resource started, so a failed call surfaces here as itself
            // rather than wrapped in a TargetInvocationException — that only wraps what Invoke throws
            // before the method runs.
            await (Task)getPage.Invoke(resource, [0, token])!;
            return null;
        }
        catch (Exception exception) when (TestClients.MissingModule(exception) is { } module)
        {
            return module;
        }
        catch (EconomicApiException exception)
        {
            return $"failed: {exception.Message}";
        }
        catch (TargetInvocationException exception)
        {
            return $"failed: {exception.InnerException?.Message}";
        }
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

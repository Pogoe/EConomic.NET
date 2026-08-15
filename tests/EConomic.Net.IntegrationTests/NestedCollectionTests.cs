using System.Collections;
using System.Reflection;
using EConomic.Exceptions;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Fetches a page from every collection reached through a parent record.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EveryResourceTests"/> walks the properties of each API surface, which is every
/// top-level collection and no nested one: a nested collection is a method taking the parent's
/// identifier, so nothing there could reach it. Until this existed, the only nested collections
/// ever sent to a server were the four that happened to appear in a write round-trip, and the
/// read-only ones had never been called at all.
/// </para>
/// <para>
/// That matters more here than for a top-level resource. A nested collection has a second thing to
/// get wrong — the parent's identifier goes into the path, and a generator that reads the wrong
/// path parameter produces a resource that compiles, maps and fails only against the server.
/// </para>
/// <para>
/// The accessors come from reflection, so one added later is covered without anyone remembering to
/// add it. An empty page is a pass: the assertion is that the call round-trips and maps, not that
/// the agreement holds data.
/// </para>
/// </remarks>
public class NestedCollectionTests
{
    [Fact]
    public async Task Every_nested_collection_returns_a_page()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        // A fresh agreement has no customers, and two of the accessors hang off one. Everything
        // else — accounting years, journals, customer groups, employees — exists on any agreement
        // and is read as it stands.
        await using var seed = new AgreementSeed(client, token);
        await seed.CustomerAsync("ZZ Probe Nested Parent");

        var accessors = Accessors(client.Rest).ToList();
        var probed = new List<string>();
        var failures = new List<string>();
        var unreachable = new List<string>();
        var elsewhere = new List<string>();
        List<(string ParentName, object Parent, MethodInfo Accessor)>? onDemo = null;

        foreach (var (parentName, parent, accessor) in accessors)
        {
            var name = $"{parentName}.{accessor.Name}";
            var outcome = await ProbeAsync(parent, accessor, token);

            if (outcome.Failure is not null)
            {
                failures.Add($"{name}: {outcome.Failure}");
                continue;
            }

            if (outcome.Probed)
            {
                probed.Add(name);
                continue;
            }

            // The parent has no records on this agreement. Employees are the case that forced this:
            // nothing on the legacy surface creates one, so the seed cannot supply a parent, and an
            // agreement that happens to have no staff would otherwise leave the accessor untested.
            // The demo agreement has them, and this only reads.
            onDemo ??= [.. Accessors(TestClients.CreateDemo().Rest)];
            var fallback = onDemo.FirstOrDefault(a =>
                a.ParentName == parentName && a.Accessor.Name == accessor.Name);

            if (fallback.Parent is null)
            {
                unreachable.Add($"{name}: {outcome.Reason}, and it is not exposed on demo");
                continue;
            }

            var retry = await ProbeAsync(fallback.Parent, fallback.Accessor, token);

            if (retry.Failure is not null)
            {
                failures.Add($"{name}, on demo: {retry.Failure}");
            }
            else if (retry.Probed)
            {
                probed.Add(name);
                elsewhere.Add($"{name} ({outcome.Reason})");
            }
            else
            {
                unreachable.Add($"{name}: {outcome.Reason}, and on demo, {retry.Reason}");
            }
        }

        // Spelled out rather than Assert.Empty, which abbreviates the collection and hides the
        // server's own explanation — the only part of the message worth reading.
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // A parent with no records anywhere is a hole in the coverage rather than a pass: the
        // accessor was never sent to a server. Naming which parent was empty makes it actionable.
        Assert.True(unreachable.Count == 0, string.Join(Environment.NewLine, unreachable));

        // A guard against the reflection silently matching nothing, which would make this pass
        // while probing zero collections.
        Assert.True(probed.Count >= 9, $"Expected every nested collection; only probed {probed.Count}.");

        // Only employees are expected to be missing. Anything else falling back means the
        // configured agreement lost records that the rest of the suite assumes are there, which
        // would otherwise move coverage onto the demo agreement's data while the run stayed green.
        var unexpected = elsewhere
            .Where(e => !e.StartsWith("Employees.", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            $"Parents empty beyond employees: {string.Join(", ", unexpected)}.");
    }

    /// <summary>Fetches the first page of one nested collection.</summary>
    /// <param name="parent">The parent resource.</param>
    /// <param name="accessor">The accessor that scopes the collection.</param>
    /// <param name="token">Cancels the requests.</param>
    /// <returns>
    /// Whether a page came back, why no parent identifier could be had if it did not, and the
    /// server's own explanation when the call itself failed.
    /// </returns>
    private static async Task<(bool Probed, string? Reason, string? Failure)> ProbeAsync(
        object parent,
        MethodInfo accessor,
        CancellationToken token)
    {
        var key = await ParentKeyAsync(parent, accessor, token);
        if (key.Failure is not null)
        {
            return (false, key.Failure, null);
        }

        var collection = accessor.Invoke(parent, [key.Value])!;
        var page = collection.GetType().GetMethod(
            "GetPageAsync",
            [typeof(int), typeof(CancellationToken)])!;

        try
        {
            // Invoke returns the Task the resource started, so a failed call surfaces as itself
            // rather than wrapped — TargetInvocationException only wraps what Invoke throws before
            // the method runs.
            await (Task)page.Invoke(collection, [0, token])!;
            return (true, null, null);
        }
        catch (EconomicApiException exception)
        {
            return (false, null, $"scoped to {key.Value}: {exception.Message}");
        }
        catch (TargetInvocationException exception)
        {
            return (false, null, $"scoped to {key.Value}: {exception.InnerException?.Message}");
        }
    }

    /// <summary>
    /// The parent's identifier, read from the first record the parent collection returns.
    /// </summary>
    /// <param name="parent">The parent resource.</param>
    /// <param name="accessor">The accessor whose parameter is being supplied.</param>
    /// <param name="token">Cancels the request.</param>
    /// <returns>The identifier, or why one could not be had.</returns>
    private static async Task<(object? Value, string? Failure)> ParentKeyAsync(
        object parent,
        MethodInfo accessor,
        CancellationToken token)
    {
        var page = parent.GetType().GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)])!;

        object result;
        try
        {
            var task = (Task)page.Invoke(parent, [0, token])!;
            await task;
            result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        }
        catch (EconomicApiException exception)
        {
            return (null, $"listing the parent failed: {exception.Message}");
        }

        var items = (IEnumerable)result.GetType().GetProperty("Items")!.GetValue(result)!;
        var first = items.Cast<object>().FirstOrDefault();

        if (first is null)
        {
            return (null, "the parent collection is empty, so nothing scopes it");
        }

        var parameter = accessor.GetParameters()[0];
        var key = KeyProperty(first.GetType(), parameter);

        if (key is null)
        {
            return (null, $"no property on {first.GetType().Name} supplies '{parameter.Name}'");
        }

        var value = key.GetValue(first);

        return value is null
            ? (null, $"{first.GetType().Name}.{key.Name} is null on the first record")
            : (value, null);
    }

    /// <summary>Finds the property on the parent's model that supplies an accessor's parameter.</summary>
    /// <param name="model">The parent's public record.</param>
    /// <param name="parameter">The accessor's single parameter.</param>
    /// <returns>The property, or <see langword="null"/> when nothing matches unambiguously.</returns>
    /// <remarks>
    /// The names line up for most of them — <c>customerNumber</c> is <c>CustomerNumber</c>. An
    /// accounting year is the exception: it is addressed by <c>{accountingYear}</c> in the path and
    /// carries the value in <c>Year</c>, so a parameter name ending in the property's name is
    /// accepted too. That second rule is required to be unambiguous; matching more than one
    /// property reports a failure rather than picking one.
    /// </remarks>
    private static PropertyInfo? KeyProperty(Type model, ParameterInfo parameter)
    {
        var candidates = model
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => Underlying(p.PropertyType) == parameter.ParameterType)
            .ToList();

        var exact = candidates.FirstOrDefault(p =>
            string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact;
        }

        var suffixed = candidates
            .Where(p => parameter.Name!.EndsWith(p.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return suffixed.Count == 1 ? suffixed[0] : null;
    }

    private static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    /// <summary>Every nested collection on the legacy surface, as parent and accessor.</summary>
    /// <param name="rest">The legacy surface.</param>
    /// <returns>The parent's property name, the parent resource, and the accessor.</returns>
    /// <remarks>
    /// An accessor is recognised by shape rather than by name: one parameter, and a resource that
    /// pages as the return type. Requiring a resource is what separates it from the query-building
    /// methods, which are also single-parameter and also return something that pages —
    /// <c>WhereRaw(string)</c> and <c>WithPageSize(int)</c> would otherwise be swept as if they
    /// were nested collections, and called with a customer number as a filter.
    /// </remarks>
    private static IEnumerable<(string ParentName, object Parent, MethodInfo Accessor)> Accessors(object rest) =>
        rest.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .SelectMany(property => property.PropertyType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Length == 1
                    && m.ReturnType.Name.EndsWith("Resource", StringComparison.Ordinal)
                    && m.ReturnType.GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)]) is not null)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .Select(m => (ParentName: property.Name, Parent: property.GetValue(rest)!, Accessor: m)));
}

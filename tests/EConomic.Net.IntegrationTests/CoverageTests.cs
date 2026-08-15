using System.Collections;
using System.Globalization;
using System.Reflection;
using EConomic.Exceptions;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Seeds the agreement and asserts every collection then holds a record.
/// </summary>
/// <remarks>
/// <para>
/// The other sweeps fetch a page from every collection and pass when the call round-trips. That is
/// worth having, but an empty page maps every property equally well — a mapping that drops a field,
/// or one that never runs at all, looks identical to one that works. This makes the difference
/// visible by putting a record in first.
/// </para>
/// <para>
/// Everything it creates is deleted again on disposal, so the agreement is left as it was found and
/// a re-run starts from the same place. Whatever cannot be created is named in
/// <see cref="AgreementCoverage.Unseedable"/> with its reason, and the two are checked against each
/// other in both directions.
/// </para>
/// </remarks>
public class CoverageTests
{
    [Fact]
    public async Task Every_collection_holds_a_record_or_is_declared_unseedable()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        await using var seed = new AgreementSeed(client, token);
        await seed.EverythingAsync();

        var populated = new List<string>();
        var declared = new List<string>();
        var failures = new List<string>();
        var moduleGated = new List<string>();

        foreach (var (name, resource) in Collections(client))
        {
            var count = await CountAsync(resource, token);

            if (count.Module is not null)
            {
                // A fact about the agreement rather than about coverage: the sweeps in
                // EveryResourceTests already prove these reach the server on demo.
                moduleGated.Add($"{name} ({count.Module})");
                continue;
            }

            if (count.Failure is not null)
            {
                failures.Add($"{name}: {count.Failure}");
                continue;
            }

            var isDeclared = AgreementCoverage.Unseedable.TryGetValue(name, out var reason);

            if (count.Items > 0)
            {
                populated.Add(name);

                // The rot guard. A collection that can now be filled must not stay on the list
                // claiming it cannot be — the entry would go on excusing an empty collection long
                // after the reason stopped applying.
                if (isDeclared)
                {
                    failures.Add(
                        $"{name} holds {count.Items} record(s), but is declared unseedable: \"{reason}\". "
                        + $"Remove it from {nameof(AgreementCoverage)}.{nameof(AgreementCoverage.Unseedable)}.");
                }

                continue;
            }

            if (isDeclared)
            {
                declared.Add($"{name}: {reason}");
                continue;
            }

            failures.Add(
                $"{name} is empty and the seed does not fill it. Either add a creator to "
                + $"{nameof(AgreementSeed)}, or declare it in "
                + $"{nameof(AgreementCoverage)}.{nameof(AgreementCoverage.Unseedable)} with the reason.");
        }

        // Spelled out rather than Assert.Empty, which abbreviates the collection and hides the
        // explanation — the only part worth reading.
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // Guards against the reflection matching nothing, which would make this pass while checking
        // no collections at all.
        Assert.True(
            populated.Count >= 59,
            $"Expected the seed to fill the agreement; only {populated.Count} collections hold records.");

        Assert.True(
            declared.Count <= AgreementCoverage.Unseedable.Count,
            $"More collections were excused than are declared: {declared.Count}.");
    }

    /// <summary>Counts the first page of one collection.</summary>
    /// <returns>How many items came back, or why none did.</returns>
    private static async Task<(int Items, string? Module, string? Failure)> CountAsync(
        object resource,
        CancellationToken token)
    {
        var page = resource.GetType().GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)])!;

        try
        {
            var task = (Task)page.Invoke(resource, [0, token])!;
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;

            // The two surfaces answer differently: the legacy one returns a page carrying Items, the
            // OpenAPI one returns the list itself.
            var items = (IEnumerable?)result.GetType().GetProperty("Items")?.GetValue(result)
                ?? result as IEnumerable;

            return (items?.Cast<object>().Count() ?? 0, null, null);
        }
        catch (Exception exception) when (TestClients.MissingModule(exception) is { } module)
        {
            return (0, module, null);
        }
        catch (EconomicApiException exception)
        {
            return (0, null, exception.Message);
        }
        catch (TargetInvocationException exception)
        {
            return (0, null, exception.InnerException?.Message);
        }
    }

    /// <summary>
    /// Every collection exposed as a property, on every API surface.
    /// </summary>
    /// <remarks>
    /// Nested collections are deliberately not swept here: they take a parent identifier, and
    /// <see cref="NestedCollectionTests"/> covers them with one. This is about the collections that
    /// hang off a surface directly.
    /// </remarks>
    private static IEnumerable<(string Name, object Resource)> Collections(EconomicClient client) =>
        typeof(EconomicClient)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.Name.EndsWith("Api", StringComparison.Ordinal))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .SelectMany(surface => surface.PropertyType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0
                    && p.PropertyType.GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)]) is not null)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => (
                    Name: string.Create(CultureInfo.InvariantCulture, $"{surface.Name}.{p.Name}"),
                    Resource: p.GetValue(surface.GetValue(client))!)));
}

using System.Collections;
using System.Globalization;
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
        EconomicClient? demo = null;

        foreach (var (parentName, parent, accessor) in accessors)
        {
            var name = $"{parentName}.{accessor.Name}";
            var outcome = await ProbeAsync(client, parent, accessor, token);

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
            demo ??= TestClients.CreateDemo();
            onDemo ??= [.. Accessors(demo.Rest)];
            var fallback = onDemo.FirstOrDefault(a =>
                a.ParentName == parentName && a.Accessor.Name == accessor.Name);

            if (fallback.Parent is null)
            {
                unreachable.Add($"{name}: {outcome.Reason}, and it is not exposed on demo");
                continue;
            }

            var retry = await ProbeAsync(demo, fallback.Parent, fallback.Accessor, token);

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
        Assert.True(probed.Count >= 19, $"Expected every nested collection; only probed {probed.Count}.");

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
        EconomicClient client,
        object parent,
        MethodInfo accessor,
        CancellationToken token)
    {
        var scopes = new List<object>();

        foreach (var parameter in accessor.GetParameters())
        {
            var scope = await ScopeAsync(client, parameter.Name!, token);
            if (scope is null)
            {
                return (false, $"nothing supplies '{parameter.Name}' on this agreement", null);
            }

            // The path parameter's type is inferred from its name, and `accountingYearPeriod` does
            // not end in "Number" so it is typed as text while the record carries an int. Both
            // reach the server as a path segment, so converting is right rather than papering over.
            scopes.Add(Convert.ChangeType(scope, parameter.ParameterType, CultureInfo.InvariantCulture));
        }

        var collection = accessor.Invoke(parent, [.. scopes])!;
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
            return (false, null, $"scoped to {string.Join(", ", scopes)}: {exception.Message}");
        }
        catch (TargetInvocationException exception)
        {
            return (false, null, $"scoped to {string.Join(", ", scopes)}: {exception.InnerException?.Message}");
        }
    }

    /// <summary>
    /// A real identifier for one of an accessor's parameters, read from the agreement.
    /// </summary>
    /// <param name="client">The client to read from.</param>
    /// <param name="parameter">The parameter's name, which is the path parameter's name.</param>
    /// <param name="token">Cancels the requests.</param>
    /// <returns>The identifier, or <see langword="null"/> when the agreement holds no such record.</returns>
    /// <remarks>
    /// Named deliberately rather than matched by reflection. A collection can now sit under three
    /// identifiers, and they do not all come from the accessor's own parent — an account's entries
    /// within a period take an account, a year and a period, which are three different collections.
    /// Anything unrecognised fails the run rather than being skipped, so a nested collection added
    /// later cannot quietly go unprobed.
    /// </remarks>
    private static async Task<object?> ScopeAsync(EconomicClient client, string parameter, CancellationToken token)
    {
        switch (parameter)
        {
            case "customerNumber":
                return (await First(client.Rest.Customers.AsQuery()))?.CustomerNumber;
            case "customerGroupNumber":
                return (await First(client.Rest.CustomerGroups.AsQuery()))?.CustomerGroupNumber;
            case "employeeNumber":
                return (await First(client.Rest.Employees.AsQuery()))?.EmployeeNumber;
            case "journalNumber":
                return (await First(client.Rest.Journals.AsQuery()))?.JournalNumber;
            case "productNumber":
                return (await First(client.Rest.Products.AsQuery()))?.ProductNumber;
            case "accountNumber":
                return (await First(client.Rest.Accounts.AsQuery()))?.AccountNumber;
            case "accountingYear":
                return (await First(client.Rest.AccountingYears.AsQuery()))?.Year;

            case "accountingYearPeriod":
                {
                    // A period is itself scoped by a year, so this is the one that has to walk down.
                    var year = (await First(client.Rest.AccountingYears.AsQuery()))?.Year;

                    return year is null
                        ? null
                        : (await First(client.Rest.AccountingYears.Periods(year).AsQuery()))?.PeriodNumber;
                }

            default:
                throw new InvalidOperationException(
                    $"Nothing here supplies '{parameter}'. Add it to {nameof(ScopeAsync)}.");
        }

        async Task<T?> First<T, TFilter, TSort>(Querying.EconomicQuery<T, TFilter, TSort> query)
        {
            var page = await query.WithPageSize(1).GetPageAsync(0, token);
            return page.Items.Count == 0 ? default : page.Items[0];
        }
    }

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
                .Where(m => m.GetParameters().Length >= 1
                    && m.ReturnType.Name.EndsWith("Resource", StringComparison.Ordinal)
                    && m.ReturnType.GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)]) is not null)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .Select(m => (ParentName: property.Name, Parent: property.GetValue(rest)!, Accessor: m)));
}

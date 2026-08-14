using System.Linq.Expressions;
using System.Reflection;
using EConomic.Exceptions;
using EConomic.Querying;
using EConomic.Rest;
using Xunit;

namespace EConomic.IntegrationTests;

/// <summary>
/// Checks every generated filter surface against the list the server publishes.
/// </summary>
/// <remarks>
/// <para>
/// The filter types are the one deliberately public part of the generated code, and they make a
/// promise: a property that compiles is a property e-conomic will filter on. That promise rests on
/// the <c>x-filterable</c> annotations in the specifications, and until now exactly three
/// properties on one resource had ever been sent to a server. The other four hundred were a claim.
/// </para>
/// <para>
/// The server settles it. A filter naming a field it does not accept comes back <c>400</c> with
/// <c>allowedFilteringFields</c> — its own list for that resource — so one deliberately bad filter
/// per resource is enough to check the whole surface. It also exercises the parsing of that list on
/// every resource rather than on the one it was written against.
/// </para>
/// <para>
/// Only one direction is a failure. The specifications are known to under-report: they mark twenty
/// properties filterable on customers where the server accepts twenty-one, omitting <c>pNumber</c>,
/// which is why <c>WhereRaw</c> exists. Claiming less than the server allows is safe. Claiming more
/// is the bug this looks for — it would turn a compile-time guarantee into a runtime <c>400</c>.
/// </para>
/// </remarks>
public class FilterSurfaceTests
{
    [Fact]
    public async Task Every_filterable_property_is_one_the_server_accepts()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        var failures = new List<string>();
        var checkedFields = 0;
        var checkedSurfaces = 0;
        var silent = new List<string>();

        foreach (var (name, query) in await QueryablesAsync(client, token))
        {
            var fields = Fields(FilterType(query.GetType()));
            if (fields.Count == 0)
            {
                // An accurate statement that e-conomic will not filter this resource on anything,
                // not a gap: the surface is emitted empty when x-filterable is absent.
                continue;
            }

            var allowed = await AllowedAsync(query, token);
            if (allowed is null)
            {
                silent.Add(name);
                continue;
            }

            checkedSurfaces++;

            foreach (var field in fields)
            {
                checkedFields++;

                if (!allowed.Contains(field, StringComparer.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{name}.{field} is offered as filterable, but the server's own list for that "
                        + $"resource is: {string.Join(", ", allowed)}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // Guards against the probe quietly covering nothing: neither the reflection finding no
        // resources, nor every server answer arriving without a list.
        Assert.True(checkedSurfaces >= 20, $"Only {checkedSurfaces} surfaces were checked.");
        Assert.True(checkedFields >= 200, $"Only {checkedFields} fields were checked.");
        Assert.True(
            silent.Count <= 3,
            $"{silent.Count} resources answered without allowedFilteringFields: {string.Join(", ", silent)}");
    }

    /// <summary>
    /// Sorts by every property each sort surface offers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sortability is a separate flag from filterability — <c>barred</c> is filterable and not
    /// sortable — so it needs its own check, and the server gives no list for it: an unsortable
    /// field is answered with "Could not parse query string sort parameter" and nothing more.
    /// </para>
    /// <para>
    /// The whole surface therefore goes into one request, since <c>sort</c> takes a list, and a
    /// rejection is bisected to name the fields responsible. That keeps a passing run to one call
    /// per resource while a failing one still reports exactly which property is at fault. It
    /// exercises the sort translation end to end at the same time: these are the expressions a
    /// consumer would write.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_sortable_property_is_one_the_server_accepts()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        var failures = new List<string>();
        var checkedFields = 0;

        foreach (var (name, query) in await QueryablesAsync(client, token))
        {
            var sortType = SortType(query.GetType());
            var fields = sortType is null
                ? []
                : sortType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<EconomicFieldAttribute>() is not null)
                    .ToArray();

            if (fields.Length == 0)
            {
                continue;
            }

            checkedFields += fields.Length;

            foreach (var rejected in await RejectedAsync(query, fields, token))
            {
                failures.Add(
                    $"{name}.{rejected.GetCustomAttribute<EconomicFieldAttribute>()!.Name} is offered as "
                    + "sortable, but the server will not sort on it.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(checkedFields >= 200, $"Only {checkedFields} sort fields were checked.");
    }

    /// <summary>The sort fields the server refuses, found by bisecting one rejected request.</summary>
    private static async Task<PropertyInfo[]> RejectedAsync(
        object query,
        PropertyInfo[] fields,
        CancellationToken token)
    {
        if (fields.Length == 0 || await AcceptsSortAsync(query, fields, token))
        {
            return [];
        }

        if (fields.Length == 1)
        {
            return fields;
        }

        var half = fields.Length / 2;

        return
        [
            .. await RejectedAsync(query, [.. fields.Take(half)], token),
            .. await RejectedAsync(query, [.. fields.Skip(half)], token),
        ];
    }

    /// <summary>Whether the server accepts a sort over exactly these fields.</summary>
    private static async Task<bool> AcceptsSortAsync(
        object query,
        PropertyInfo[] fields,
        CancellationToken token)
    {
        var sorted = query;

        for (var i = 0; i < fields.Length; i++)
        {
            var method = sorted.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m => m.Name == (i == 0 ? "OrderBy" : "ThenBy") && m.GetParameters().Length == 1);

            sorted = method.Invoke(sorted, [Selector(fields[i])])!;
        }

        var getPage = sorted.GetType().GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)])!;

        try
        {
            await (Task)getPage.Invoke(sorted, [0, token])!;
            return true;
        }
        catch (EconomicApiException)
        {
            return false;
        }
    }

    /// <summary>The lambda a consumer would write to sort by this property.</summary>
    private static LambdaExpression Selector(PropertyInfo field)
    {
        var parameter = Expression.Parameter(field.DeclaringType!, "s");

        return Expression.Lambda(
            typeof(Func<,>).MakeGenericType(field.DeclaringType!, typeof(EconomicSortField)),
            Expression.Property(parameter, field),
            parameter);
    }

    /// <summary>
    /// The server's list for a resource, or <see langword="null"/> if it did not publish one.
    /// </summary>
    /// <remarks>
    /// The filter is raw on purpose. Naming a field that cannot exist is the whole point, and the
    /// generated surface — correctly — offers no way to write one.
    /// </remarks>
    private static async Task<IReadOnlyList<string>?> AllowedAsync(object query, CancellationToken token)
    {
        var type = query.GetType();
        var rejected = type.GetMethod("WhereRaw", [typeof(string)])!.Invoke(query, ["zzzNoSuchField$eq:1"])!;
        var getPage = rejected.GetType().GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)])!;

        try
        {
            await (Task)getPage.Invoke(rejected, [0, token])!;
        }
        catch (EconomicApiException exception)
        {
            return exception.AllowedFilteringFields;
        }

        return null;
    }

    /// <summary>Every queryable the client exposes, nested collections included.</summary>
    /// <remarks>
    /// A nested collection needs a parent to hang off, and the parent has to exist on the
    /// agreement. Anything unrecognised fails rather than being skipped, so a nested resource added
    /// later cannot quietly drop out of this.
    /// </remarks>
    private static async Task<List<(string Name, object Query)>> QueryablesAsync(
        EconomicClient client,
        CancellationToken token)
    {
        var queryables = new List<(string, object)>();

        foreach (var property in typeof(EconomicClient)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var value = property.GetValue(client);
            if (value is null || FilterType(value.GetType()) is null)
            {
                continue;
            }

            queryables.Add((property.Name, value));

            foreach (var nested in property.PropertyType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.ReturnType.Name.EndsWith("Resource", StringComparison.Ordinal)
                    && m.GetParameters() is [{ ParameterType.Name: nameof(Int32) }])
                .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var parent = await ParentAsync(client, property.Name, token);
                queryables.Add((
                    $"{property.Name}.{nested.Name}",
                    nested.Invoke(value, [parent])!));
            }
        }

        return queryables;
    }

    /// <summary>An identifier a nested collection can hang off.</summary>
    private static async Task<int> ParentAsync(EconomicClient client, string owner, CancellationToken token) =>
        owner switch
        {
            "Customers" => (await client.Customers.GetPageAsync(0, token)).Items[0].CustomerNumber,
            "Journals" => (await client.Journals.GetPageAsync(0, token)).Items[0].JournalNumber,
            _ => throw new InvalidOperationException(
                $"{owner} has a nested collection this test does not know how to reach."),
        };

    /// <summary>The filter type of a query or resource, if it is one.</summary>
    private static Type? FilterType(Type type) => Surfaces(type) is [_, var filter, _] ? filter : null;

    /// <summary>The sort type of a query or resource, if it is one.</summary>
    private static Type? SortType(Type type) => Surfaces(type) is [_, _, var sort] ? sort : null;

    /// <summary>The generic arguments of the query a resource or query exposes.</summary>
    private static Type[]? Surfaces(Type type)
    {
        var query = type.Name.StartsWith("EconomicQuery", StringComparison.Ordinal)
            ? type
            : type.GetMethod("WhereRaw", [typeof(string)])?.ReturnType;

        return query?.IsGenericType == true ? query.GetGenericArguments() : null;
    }

    /// <summary>The field names a filter surface offers, as e-conomic spells them.</summary>
    private static IReadOnlyList<string> Fields(Type? filter) =>
        filter is null
            ? []
            : [.. filter.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<EconomicFieldAttribute>()?.Name)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal)];
}

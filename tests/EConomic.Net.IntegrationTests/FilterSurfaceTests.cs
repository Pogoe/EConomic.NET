using System.Globalization;
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
    /// <summary>The operator each member of a filter field translates to.</summary>
    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["op_Equality"] = "$eq:",
        ["op_Inequality"] = "$ne:",
        ["op_LessThan"] = "$lt:",
        ["op_LessThanOrEqual"] = "$lte:",
        ["op_GreaterThan"] = "$gt:",
        ["op_GreaterThanOrEqual"] = "$gte:",
        [nameof(TextField.Like)] = "$like:",
        [nameof(NumericField<int>.In)] = "$in:",
        [nameof(NumericField<int>.NotIn)] = "$nin:",
        [nameof(EconomicFilterField.IsNull)] = "$eq:$null:",
    };

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
        // resources, nor every server answer arriving without a list. Set to what the suite actually
        // covers rather than to a round number, so coverage shrinking fails here too — a service
        // dropping out of the reflection would otherwise leave a green run that checked less.
        Assert.True(checkedSurfaces >= 23, $"Only {checkedSurfaces} surfaces were checked.");
        Assert.True(checkedFields >= 391, $"Only {checkedFields} fields were checked.");
        // Only the legacy API publishes the list, so only it can be checked this way. Asserting
        // where the silence comes from keeps that honest: a legacy resource that stopped publishing
        // one would land here and fail, rather than quietly dropping out of the check.
        Assert.All(silent, name => Assert.StartsWith("Open.", name, StringComparison.Ordinal));
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
        Assert.True(checkedFields >= 519, $"Only {checkedFields} sort fields were checked.");
    }

    /// <summary>
    /// Sends every operator each filter surface offers, on the property it offers it for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filterability and operators are two separate claims, and only the first had ever been
    /// checked. A field being filterable does not mean every operator works on it: the OpenAPI
    /// services publish a per-property operator list, and the legacy schemas publish no operators at
    /// all, so there they are inferred from the property's type — a guess, and documented as one.
    /// Both are the kind of claim that turns into a runtime <c>400</c> on a surface whose entire
    /// purpose is to make that a compile error.
    /// </para>
    /// <para>
    /// One request per clause, which is slow and deliberate. Joining the clauses with <c>$and:</c>
    /// the way the sort check joins its fields does not work here: e-conomic short-circuits a
    /// conjunction, so a clause that answers <c>500</c> on its own comes back <c>200</c> once
    /// another clause ahead of it has already excluded every row. That was not a guess — a
    /// twenty-clause filter containing <c>assetGroupNumber$eq:</c> passed while the same clause
    /// alone failed, which is exactly the false pass this check exists to avoid. Sorting cannot
    /// hide a field the same way, because ordering has to touch every field named.
    /// </para>
    /// <para>
    /// These are real expressions, not hand-written filter strings, so the translation of every
    /// operator is exercised at the same time — including <c>Like</c>, <c>In</c>, <c>NotIn</c> and
    /// <c>IsNull</c>, which have no operator syntax to fall back on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_filter_operator_is_one_the_server_accepts()
    {
        TestClients.SkipUnlessConfigured();

        var client = TestClients.Create();
        var token = TestContext.Current.CancellationToken;

        var failures = new List<string>();
        var checkedClauses = 0;

        foreach (var (name, query) in await QueryablesAsync(client, token))
        {
            var clauses = Clauses(FilterType(query.GetType()), Generated(query.GetType()));
            if (clauses.Count == 0)
            {
                continue;
            }

            checkedClauses += clauses.Count;

            foreach (var clause in clauses)
            {
                if (!await AcceptsFilterAsync(query, clause, token))
                {
                    failures.Add(
                        $"{name} offers {clause.Description}, but the server will not filter on it.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(checkedClauses >= 4636, $"Only {checkedClauses} filter clauses were checked.");
    }

    /// <summary>Whether the server accepts a filter made of exactly this clause.</summary>
    private static async Task<bool> AcceptsFilterAsync(
        object query,
        Clause clause,
        CancellationToken token)
    {
        var filter = FilterType(query.GetType())!;
        var parameter = Expression.Parameter(filter, "f");

        var predicate = Expression.Lambda(
            typeof(Func<,>).MakeGenericType(filter, typeof(bool)), Body(parameter, clause), parameter);

        var where = query.GetType().GetMethod("Where", BindingFlags.Public | BindingFlags.Instance)!;
        var filtered = where.Invoke(query, [predicate])!;

        try
        {
            await OnePageAsync(query, filtered, token);
            return true;
        }
        catch (EconomicApiException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches one page of a filtered query, by whichever listing the collection publishes.
    /// </summary>
    /// <remarks>
    /// Not every collection has both. The matched booked-entry pairs publish a cursor and nothing
    /// else, so the resource offers no <c>GetPageAsync</c> to call — which is the surface being
    /// honest, and the reason this asks the resource what it has rather than assuming.
    /// </remarks>
    private static async Task OnePageAsync(object resource, object filtered, CancellationToken token)
    {
        if (resource.GetType().GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)]) is not null)
        {
            var page = filtered.GetType().GetMethod("GetPageAsync", [typeof(int), typeof(CancellationToken)])!;
            await (Task)page.Invoke(filtered, [0, token])!;
            return;
        }

        var cursor = filtered.GetType().GetMethod("GetCursorPageAsync", [typeof(string), typeof(CancellationToken)])!;
        await (Task)cursor.Invoke(filtered, [null, token])!;
    }

    /// <summary>The expression a consumer would write for one clause.</summary>
    private static Expression Body(ParameterExpression parameter, Clause clause)
    {
        var field = Expression.Property(parameter, clause.Field);
        var parameters = clause.Method.GetParameters();

        if (clause.Method.IsStatic)
        {
            var operand = parameters[1].ParameterType;

            var value = clause.Text is not null && operand == typeof(string)
                ? clause.Text
                : Sample(operand);

            return Expression.MakeBinary(
                Kind(clause.Method.Name),
                field,
                Expression.Constant(value, operand),
                liftToNull: false,
                clause.Method);
        }

        return clause.Method.Name switch
        {
            nameof(EconomicFilterField.IsNull) => Expression.Call(field, clause.Method),
            nameof(TextField.Like) => Expression.Call(
                field, clause.Method, Expression.Constant(clause.Text ?? "zz")),
            // In and NotIn take a params array, which the translator reads as a NewArrayExpression.
            _ => Expression.Call(
                field,
                clause.Method,
                Expression.NewArrayInit(
                    parameters[0].ParameterType.GetElementType()!,
                    Expression.Constant(
                        Sample(parameters[0].ParameterType.GetElementType()!),
                        parameters[0].ParameterType.GetElementType()!))),
        };
    }

    /// <summary>
    /// A value of the right type to compare against.
    /// </summary>
    /// <remarks>
    /// Deliberately a value nothing is likely to match: this asks whether the server will parse the
    /// clause, not what it returns, and an empty result is the cheapest answer to get back.
    /// </remarks>
    private static object Sample(Type type)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;

        if (bare == typeof(string))
        {
            return "zz";
        }

        if (bare.IsEnum)
        {
            return Enum.GetValues(bare).GetValue(0)!;
        }

        return bare switch
        {
            _ when bare == typeof(bool) => true,
            _ when bare == typeof(DateOnly) => new DateOnly(2020, 1, 1),
            _ when bare == typeof(DateTime) => new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            _ when bare == typeof(DateTimeOffset) => new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            _ when bare == typeof(TimeSpan) => TimeSpan.Zero,
            _ when bare == typeof(Guid) => Guid.Empty,
            _ => Convert.ChangeType(1, bare, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Every clause a filter surface offers, as property and operator.</summary>
    private static IReadOnlyList<Clause> Clauses(Type? filter, Type? generated) =>
        filter is null
            ? []
            : [.. filter.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Property: p, Field: p.GetCustomAttribute<EconomicFieldAttribute>()?.Name))
                .Where(p => p.Field is not null)
                .OrderBy(p => p.Field, StringComparer.Ordinal)
                .SelectMany(p => p.Property.PropertyType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                    .Where(m => Operators.ContainsKey(m.Name))
                    .OrderBy(m => m.Name, StringComparer.Ordinal)
                    .Select(m => new Clause(
                        p.Property, m, $"{p.Field}{Operators[m.Name]}", Member(generated, p.Property.Name))))];

    /// <summary>
    /// A value the property will accept, where the type alone does not say what that is.
    /// </summary>
    /// <remarks>
    /// The public models carry an enumerated property as text, because the generated enum is
    /// internal. The names are still reachable through the mapping the facade generates: a
    /// resource's source class converts the public record back to the generated one, and that
    /// record still declares the enum. Taking a real member from it is what keeps this probe asking
    /// about the operator rather than about the value.
    /// </remarks>
    private static string? Member(Type? generated, string property) =>
        generated?.GetProperty(property)?.PropertyType is { } type
        && (Nullable.GetUnderlyingType(type) ?? type) is { IsEnum: true } enumeration
            ? Enum.GetNames(enumeration).FirstOrDefault()
            : null;

    /// <summary>
    /// The generated record a resource maps its public one to, if the facade generated one.
    /// </summary>
    /// <remarks>
    /// Found by name rather than by a list, so a resource added later is covered. Returning
    /// <see langword="null"/> is fine — it only means the synthetic values are used, which is
    /// correct for every property whose type says what it accepts.
    /// </remarks>
    private static Type? Generated(Type queryable)
    {
        if (Surfaces(queryable) is not [{ } resource, ..])
        {
            return null;
        }

        return typeof(EconomicClient).Assembly
            .GetType($"{resource.Namespace}.{resource.Name}Source")
            ?.GetMethod("ToGenerated", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?.ReturnType;
    }

    private static ExpressionType Kind(string name) => name switch
    {
        "op_Equality" => ExpressionType.Equal,
        "op_Inequality" => ExpressionType.NotEqual,
        "op_LessThan" => ExpressionType.LessThan,
        "op_LessThanOrEqual" => ExpressionType.LessThanOrEqual,
        "op_GreaterThan" => ExpressionType.GreaterThan,
        _ => ExpressionType.GreaterThanOrEqual,
    };

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

        try
        {
            await OnePageAsync(query, rejected, token);
        }
        catch (EconomicApiException exception)
        {
            // The legacy API answers a bad filter with the fields it would have accepted. The
            // OpenAPI services do not — they name the offending property and stop — so an empty
            // list means "no list published" rather than "nothing is filterable".
            return exception.AllowedFilteringFields is { Count: > 0 } allowed ? allowed : null;
        }

        return null;
    }

    /// <summary>Every queryable the client can actually reach, nested collections included.</summary>
    /// <remarks>
    /// <para>
    /// A nested collection needs a parent to hang off, and the parent has to exist on the
    /// agreement. Anything unrecognised fails rather than being skipped, so a nested resource added
    /// later cannot quietly drop out of this.
    /// </para>
    /// <para>
    /// Each is then asked for one unfiltered page, because a collection belonging to a module the
    /// agreement has not bought answers <c>403</c> to everything — and this probe reads a refusal as
    /// "the server will not filter on that", which would report the whole projects service as broken.
    /// Where the configured agreement cannot reach a collection, the same collection on the demo
    /// agreement stands in, so it is verified rather than skipped. A collection neither can reach
    /// fails the run: silently dropping one would leave a surface nothing had ever sent.
    /// </para>
    /// </remarks>
    private static async Task<List<(string Name, object Query)>> QueryablesAsync(
        EconomicClient client,
        CancellationToken token)
    {
        var reachable = new List<(string Name, object Query)>();
        List<(string Name, object Query)>? onDemo = null;

        foreach (var candidate in await ExposedAsync(client, token))
        {
            if (await ModuleAsync(candidate.Query, token) is null)
            {
                reachable.Add(candidate);
                continue;
            }

            onDemo ??= await ExposedAsync(TestClients.CreateDemo(), token);
            var fallback = onDemo.FirstOrDefault(q => q.Name == candidate.Name);
            var refused = fallback.Query is null ? "not exposed" : await ModuleAsync(fallback.Query, token);

            if (refused is not null)
            {
                throw new InvalidOperationException(
                    $"{candidate.Name} is out of reach on the configured agreement and on the demo "
                    + $"agreement too ({refused}), so its filter and sort surfaces cannot be checked "
                    + "against a server. Point the tests at an agreement that has the module.");
            }

            reachable.Add(fallback);
        }

        return reachable;
    }

    /// <summary>The module a collection needs and the agreement lacks, if that is why it refuses.</summary>
    /// <remarks>
    /// Through <c>AsQuery()</c> where the queryable is a resource, rather than the resource itself:
    /// a cursor-only collection publishes no <c>GetPageAsync</c> on its resource, and only the query
    /// offers the cursor call <see cref="OnePageAsync"/> falls back to. Some surfaces expose a query
    /// directly, which has no <c>AsQuery</c> and needs none.
    /// </remarks>
    private static async Task<string?> ModuleAsync(object query, CancellationToken token)
    {
        var unfiltered = query.GetType()
            .GetMethod("AsQuery", BindingFlags.Public | BindingFlags.Instance)
            ?.Invoke(query, []) ?? query;

        try
        {
            await OnePageAsync(query, unfiltered, token);
            return null;
        }
        catch (EconomicApiException exception) when (TestClients.MissingModule(exception) is { } module)
        {
            return module;
        }
    }

    /// <summary>Every queryable one client exposes, whether or not the agreement can reach it.</summary>
    private static async Task<List<(string Name, object Query)>> ExposedAsync(
        EconomicClient client,
        CancellationToken token)
    {
        var queryables = new List<(string, object)>();

        // Every API surface the client exposes — Rest today, Open as it lands — then everything
        // queryable on each.
        var surfaces = typeof(EconomicClient)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.Name.EndsWith("Api", StringComparison.Ordinal))
            .Select(p => p.GetValue(client)!)
            .ToList();

        // A collection scoped by a path parameter is a method on the surface rather than a property,
        // because it cannot be addressed without that identifier. The parent need not exist: these
        // answer an empty listing rather than a 404, which is what makes probing them possible on an
        // agreement that has none — verified against price groups, of which this agreement has zero.
        foreach (var (surface, scoped) in surfaces
            .SelectMany(s => s.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => (Surface: s, Method: m)))
            .Where(x => x.Method.ReturnType.Name.EndsWith("Resource", StringComparison.Ordinal)
                && x.Method.GetParameters() is [{ ParameterType.Name: nameof(Int32) }])
            .OrderBy(x => x.Method.Name, StringComparer.Ordinal))
        {
            queryables.Add((
                $"{SurfaceName(surface)}.{scoped.Name}",
                scoped.Invoke(surface, [Scope(scoped.Name)])!));
        }

        foreach (var (surface, property) in surfaces
            .SelectMany(s => s.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Surface: s, Property: p)))
            .OrderBy(x => x.Property.Name, StringComparer.Ordinal))
        {
            var value = property.GetValue(surface);
            if (value is null || FilterType(value.GetType()) is null)
            {
                continue;
            }

            queryables.Add(($"{SurfaceName(surface)}.{property.Name}", value));

            foreach (var nested in property.PropertyType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.ReturnType.Name.EndsWith("Resource", StringComparison.Ordinal)
                    && m.GetParameters() is [{ ParameterType.Name: nameof(Int32) }])
                .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                var parent = await ParentAsync(client, property.Name, token);
                queryables.Add((
                    $"{SurfaceName(surface)}.{property.Name}.{nested.Name}",
                    nested.Invoke(value, [parent])!));
            }
        }

        return queryables;
    }

    /// <summary>Which API surface an object came from, for the failure messages.</summary>
    private static string SurfaceName(object surface) =>
        surface.GetType().Name.Replace("Economic", string.Empty, StringComparison.Ordinal)
            .Replace("Api", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// An identifier a scoped collection can hang off.
    /// </summary>
    /// <remarks>
    /// One rather than a lookup: these collections answer an empty listing for a parent that does
    /// not exist, and this check is about whether the server parses the filter, not about what the
    /// filter matches. Naming each one deliberately rather than defaulting keeps a scoped collection
    /// added later from silently inheriting an assumption nobody checked.
    /// </remarks>
    private static int Scope(string collection) =>
        collection switch
        {
            "ProductSpecialPrices" or "ProductSpecialPricesInCurrency" or "ProductZones" => 1,
            _ => throw new InvalidOperationException(
                $"{collection} is scoped by an identifier this test does not know how to supply."),
        };

    /// <summary>An identifier a nested collection can hang off.</summary>
    private static async Task<int> ParentAsync(EconomicClient client, string owner, CancellationToken token) =>
        owner switch
        {
            "Customers" => (await client.Rest.Customers.GetPageAsync(0, token)).Items[0].CustomerNumber,
            "Journals" => (await client.Rest.Journals.GetPageAsync(0, token)).Items[0].JournalNumber,
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

    /// <summary>One operator a filter surface offers on one property.</summary>
    /// <param name="Field">The filter surface property.</param>
    /// <param name="Method">The operator, comparison or method, that property exposes.</param>
    /// <param name="Description">How the clause reads in e-conomic's own syntax.</param>
    /// <param name="Text">
    /// A value the server will recognise, where an arbitrary one will not do. An enumerated property
    /// is text on the public model, and e-conomic rejects a name outside the set: <c>type$ne:zz</c>
    /// answers "Requested value 'zz' was not found" while <c>type$eq:zz</c> is tolerated and matches
    /// nothing. Probing with a synthetic value therefore reported an operator as broken that works
    /// perfectly well.
    /// </param>
    private sealed record Clause(PropertyInfo Field, MethodInfo Method, string Description, string? Text = null);

    /// <summary>The field names a filter surface offers, as e-conomic spells them.</summary>
    private static IReadOnlyList<string> Fields(Type? filter) =>
        filter is null
            ? []
            : [.. filter.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.GetCustomAttribute<EconomicFieldAttribute>()?.Name)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal)];
}

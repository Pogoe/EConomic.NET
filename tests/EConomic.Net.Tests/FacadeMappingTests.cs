using System.Collections;
using System.Globalization;
using System.Reflection;
using Xunit;

namespace EConomic.Tests;

/// <summary>
/// Maps a fully populated response onto every resource the facade exposes.
/// </summary>
/// <remarks>
/// <para>
/// The integration suite fetches a page from all of them, but several — sent and archived orders
/// and quotes, sent invoices — are empty on any agreement this library can build up, because the
/// legacy API publishes no endpoint that promotes a draft into them. Those resources were therefore
/// proven to round-trip and nothing more: an empty collection maps every property equally well,
/// whether the mapping is right or missing entirely.
/// </para>
/// <para>
/// This fills a generated response in, property by property, and asserts that every property the
/// public record declares comes out populated. It is the same failure the reflection sweep in the
/// integration suite guards against — one wrong turn in a generator repeated across thirty-seven
/// mappings — caught for the resources no amount of live data would reach.
/// </para>
/// <para>
/// What it deliberately does not claim: that the JSON names and formats are right. Those come from
/// the specifications through NSwag, and a specification that disagrees with the server cannot be
/// caught by a test that treats it as the truth — the <c>full-date</c> properties that really
/// carry timestamps are exactly that case, and only a live response settled them.
/// </para>
/// </remarks>
public class FacadeMappingTests
{
    [Fact]
    public void Every_resource_maps_every_property_it_declares()
    {
        var failures = new List<string>();
        var mapped = new List<string>();

        foreach (var map in Mappers())
        {
            var sourceType = map.GetParameters()[0].ParameterType;
            var source = Populated(sourceType, []);

            object result;
            try
            {
                result = map.Invoke(null, [source])!;
            }
            catch (TargetInvocationException exception)
            {
                failures.Add($"{map.ReturnType.Name}: mapping threw {exception.InnerException}");
                continue;
            }

            Inspect(result, map.ReturnType.Name, failures);
            mapped.Add(map.ReturnType.Name);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // A guard against the reflection silently matching nothing, which would make this pass
        // while mapping zero resources.
        Assert.True(mapped.Count >= 52, $"Expected every resource; only mapped {mapped.Count}.");
    }

    /// <summary>
    /// Confirms the check above can fail.
    /// </summary>
    /// <remarks>
    /// A test that inspects thirty-seven types by reflection and reports nothing is indistinguishable
    /// from one that inspects nothing at all, so this pins that both failures it looks for are
    /// actually detected.
    /// </remarks>
    [Fact]
    public void The_check_reports_what_was_left_unmapped()
    {
        var failures = new List<string>();

        Inspect(new Probe { Mapped = "value", Lines = ["line"] }, "Probe", failures);
        Assert.Empty(failures);

        Inspect(new Probe { Mapped = "value" }, "Probe", failures);
        Inspect(new Probe { Lines = ["line"] }, "Probe", failures);

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.Contains("Probe.Lines", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Probe.Mapped", StringComparison.Ordinal));
    }

    /// <summary>The generated-to-public mapping of every resource, found by reflection.</summary>
    /// <remarks>
    /// Taken from the assembly rather than a list here, so a resource added later is covered
    /// without anyone remembering to add it.
    /// </remarks>
    private static IEnumerable<MethodInfo> Mappers() =>
        typeof(EconomicClient).Assembly
            .GetTypes()
            // Both surfaces: the legacy sources are named ...PageSource and the OpenAPI ones
            // ...Source, and the common suffix is what keeps this from needing a list per surface.
            .Where(t => t.Name.EndsWith("Source", StringComparison.Ordinal))
            .Select(t => t.GetMethod("Map", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            .OfType<MethodInfo>()
            .Where(m => m.GetParameters().Length == 1)
            .OrderBy(m => m.ReturnType.Name, StringComparer.Ordinal);

    /// <summary>Builds a generated response with every property set to something recognisable.</summary>
    /// <param name="type">The generated type to fill.</param>
    /// <param name="inProgress">The types being filled further up, so a self-referencing one stops.</param>
    /// <returns>The populated instance.</returns>
    private static object Populated(Type type, HashSet<Type> inProgress)
    {
        var instance = Activator.CreateInstance(type)!;

        if (!inProgress.Add(type))
        {
            // A type that contains itself: leave the inner copy empty rather than recurring forever.
            return instance;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (Value(property.PropertyType, inProgress) is { } value)
            {
                property.SetValue(instance, value);
            }
        }

        inProgress.Remove(type);
        return instance;
    }

    /// <summary>A recognisable value for one property.</summary>
    /// <remarks>
    /// Never the type's default, because several mappings read a default as "absent" — an unset
    /// date is mapped to <see langword="null"/> rather than to 1 January year one. A zero here
    /// would therefore look like a missing mapping.
    /// </remarks>
    private static object? Value(Type type, HashSet<Type> inProgress)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target.IsEnum)
        {
            return Enum.GetValues(target).Cast<object>().FirstOrDefault(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0)
                ?? Enum.GetValues(target).GetValue(0);
        }

        if (target == typeof(string)) { return "probe"; }
        if (target == typeof(Uri)) { return new Uri("https://restapi.e-conomic.com/probe/7"); }
        if (target == typeof(bool)) { return true; }
        if (target == typeof(DateOnly)) { return new DateOnly(2026, 4, 7); }
        if (target == typeof(DateTime)) { return new DateTime(2026, 4, 7, 8, 53, 29, DateTimeKind.Utc); }
        if (target == typeof(DateTimeOffset)) { return new DateTimeOffset(2026, 4, 7, 8, 53, 29, TimeSpan.Zero); }
        if (target == typeof(decimal)) { return 1.5m; }
        if (target == typeof(double)) { return 1.5d; }
        if (target == typeof(float)) { return 1.5f; }
        if (target == typeof(Guid)) { return Guid.Parse("00000000-0000-0000-0000-0000000007a1"); }
        if (target.IsPrimitive) { return Convert.ChangeType(7, target, CultureInfo.InvariantCulture); }

        if (target.IsGenericType && typeof(IEnumerable).IsAssignableFrom(target))
        {
            // NSwag gives every generated class an `AdditionalProperties` dictionary for whatever
            // the schema did not declare. Nothing is mapped from it, so there is nothing to fill.
            if (target.GetGenericArguments().Length != 1)
            {
                return null;
            }

            var element = target.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;

            if (Value(element, inProgress) is { } item)
            {
                list.Add(item);
            }

            return list;
        }

        if (target.IsClass && !target.IsAbstract && target != typeof(object)
            && target.GetConstructor(Type.EmptyTypes) is not null)
        {
            return Populated(target, inProgress);
        }

        return null;
    }

    /// <summary>Asserts that everything the public record declares was filled in.</summary>
    private static void Inspect(object value, string path, List<string> failures)
    {
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var current = property.GetValue(value);
            var name = $"{path}.{property.Name}";

            switch (current)
            {
                case null:
                    failures.Add($"{name} was not mapped, though the response carried a value.");
                    break;

                case string text when text.Length == 0:
                    failures.Add($"{name} mapped to an empty string.");
                    break;

                case IEnumerable items and not string:
                    var first = items.Cast<object>().FirstOrDefault();
                    if (first is null)
                    {
                        failures.Add($"{name} mapped to an empty collection, though the response carried one item.");
                    }
                    else if (IsFacadeType(first.GetType()))
                    {
                        Inspect(first, $"{name}[0]", failures);
                    }

                    break;

                default:
                    if (IsFacadeType(current.GetType()))
                    {
                        Inspect(current, name, failures);
                    }
                    else if (current.GetType().IsValueType && current.Equals(Activator.CreateInstance(current.GetType())))
                    {
                        failures.Add($"{name} mapped to its default value, though the response carried one.");
                    }

                    break;
            }
        }
    }

    /// <summary>A stand-in for a public record, used only to prove the check above can fail.</summary>
    private sealed record Probe
    {
        public string? Mapped { get; init; }

        public IReadOnlyList<string> Lines { get; init; } = [];
    }

    /// <summary>Whether this is one of the library's own records, and so worth recurring into.</summary>
    private static bool IsFacadeType(Type type) =>
        type.IsClass && type != typeof(string) && type != typeof(Uri)
        && type.Namespace?.StartsWith("EConomic", StringComparison.Ordinal) == true;
}

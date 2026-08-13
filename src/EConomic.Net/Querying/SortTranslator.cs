using System.Linq.Expressions;
using System.Reflection;

namespace EConomic.Querying;

/// <summary>
/// A property on a generated sort surface.
/// </summary>
/// <remarks>
/// Sortability is a separate flag from filterability in the specs, so it gets its own surface: a
/// property can be sortable without being filterable, and vice versa. Selecting one that e-conomic
/// will not sort by is therefore a compile error rather than silently ignored ordering.
/// </remarks>
public sealed class EconomicSortField
{
    internal EconomicSortField()
    {
    }
}

/// <summary>Direction of a sort clause.</summary>
public enum SortDirection
{
    /// <summary>Ascending, e-conomic's default, rendered as <c>sort=name</c>.</summary>
    Ascending,

    /// <summary>Descending, rendered as <c>sort=-name</c>.</summary>
    Descending,
}

/// <summary>One ordering clause.</summary>
/// <param name="Field">The field name as e-conomic spells it.</param>
/// <param name="Direction">Which way to order.</param>
public readonly record struct SortClause(string Field, SortDirection Direction)
{
    /// <summary>Renders the clause as it appears in a <c>sort=</c> value.</summary>
    /// <returns>The field name, prefixed with <c>-</c> when descending.</returns>
    public override string ToString() =>
        Direction == SortDirection.Descending ? $"-{Field}" : Field;
}

/// <summary>Translates sort selectors into e-conomic's <c>sort=</c> syntax.</summary>
public static class SortTranslator
{
    /// <summary>Reads the e-conomic field name out of a sort selector.</summary>
    /// <typeparam name="TSort">The generated sort surface.</typeparam>
    /// <param name="selector">A selector such as <c>c =&gt; c.Name</c>.</param>
    /// <returns>The field name as e-conomic spells it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="selector"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The selector is not a direct property access.</exception>
    public static string FieldName<TSort>(Expression<Func<TSort, EconomicSortField>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (selector.Body is not MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression })
        {
            throw new NotSupportedException(
                $"'{selector.Body}' is not a sortable field. Select a property directly, as in c => c.Name.");
        }

        return property.GetCustomAttribute<EconomicFieldAttribute>()?.Name
            ?? throw new NotSupportedException(
                $"'{property.DeclaringType?.Name}.{property.Name}' has no {nameof(EconomicFieldAttribute)}.");
    }

    /// <summary>Renders a set of clauses into a <c>sort=</c> value.</summary>
    /// <param name="clauses">The clauses, in priority order.</param>
    /// <returns>The comma-separated value, without the <c>sort=</c> prefix.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clauses"/> is <see langword="null"/>.</exception>
    public static string Render(IEnumerable<SortClause> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        return string.Join(",", clauses);
    }
}

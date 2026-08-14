using System.Diagnostics.CodeAnalysis;

namespace EConomic.Querying;

/// <summary>
/// Base type for the properties of a generated filter surface.
/// </summary>
/// <remarks>
/// <para>
/// A filter surface exposes only the properties e-conomic will actually filter on, and types each
/// one so that only its permitted operators are reachable. Writing a filter against a
/// non-filterable property therefore fails to compile, as does using <c>like</c> on a property
/// that does not support it — instead of arriving as a <c>400</c> after a round trip. On the
/// Customers API that matters: 80 of 99 properties are not filterable and only 2 support
/// <c>like</c>.
/// </para>
/// <para>
/// These operators are never executed. They exist so a lambda compiles into an expression tree,
/// which the translator then reads. Invoking one directly is a bug, so they throw.
/// </para>
/// </remarks>
public abstract class EconomicFilterField
{
    private protected EconomicFilterField()
    {
    }

    /// <summary>Matches records where the property is absent, e-conomic's <c>$null:</c>.</summary>
    /// <returns>Never returns; only meaningful inside a filter expression.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Must be an instance member so it reads as field.IsNull() in a lambda.")]
    public bool IsNull() => throw NotAnExpression();

    /// <inheritdoc />
    public override bool Equals(object? obj) => throw NotAnExpression();

    /// <inheritdoc />
    public override int GetHashCode() => throw NotAnExpression();

    private protected static InvalidOperationException NotAnExpression() =>
        new("Filter fields describe a query and cannot be evaluated. Use them only inside a Where or OrderBy lambda.");
}

/// <summary>A text property supporting <c>$eq:</c>, <c>$ne:</c> and <c>$like:</c>.</summary>
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates",
    Justification = "The operators are markers read out of an expression tree, never invoked.")]
public sealed class TextField : EconomicFilterField
{
    internal TextField()
    {
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => throw NotAnExpression();

    /// <inheritdoc />
    public override int GetHashCode() => throw NotAnExpression();

    /// <summary>Translates to <c>$eq:</c>, or <c>$null:</c> when compared with <see langword="null"/>.</summary>
    public static bool operator ==(TextField field, string? value) => throw NotAnExpression();

    /// <summary>Translates to <c>$ne:</c>.</summary>
    public static bool operator !=(TextField field, string? value) => throw NotAnExpression();

    /// <summary>
    /// Translates to <c>$like:</c>. A pattern containing no <c>*</c> is a "contains" match — the
    /// server wraps it in wildcards — so <c>Like("Acme")</c> and <c>Like("Acme*")</c> mean
    /// different things.
    /// </summary>
    /// <param name="pattern">The pattern, where <c>*</c> is the wildcard.</param>
    /// <returns>Never returns; only meaningful inside a filter expression.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Must be an instance member so it reads as field.Like(...) in a lambda.")]
    public bool Like(string pattern) => throw NotAnExpression();
}

/// <summary>A boolean property supporting <c>$eq:</c> and <c>$ne:</c>.</summary>
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates",
    Justification = "The operators are markers read out of an expression tree, never invoked.")]
public sealed class BooleanField : EconomicFilterField
{
    internal BooleanField()
    {
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => throw NotAnExpression();

    /// <inheritdoc />
    public override int GetHashCode() => throw NotAnExpression();

    /// <summary>Translates to <c>$eq:</c>.</summary>
    public static bool operator ==(BooleanField field, bool value) => throw NotAnExpression();

    /// <summary>Translates to <c>$ne:</c>.</summary>
    public static bool operator !=(BooleanField field, bool value) => throw NotAnExpression();
}

/// <summary>
/// A property supporting only <c>$eq:</c> and <c>$ne:</c>.
/// </summary>
/// <typeparam name="T">The value type, e.g. <see cref="string"/>.</typeparam>
/// <remarks>
/// The OpenAPI services publish what each property accepts, and for several it is equality and
/// nothing else. This is what that maps to: a text property with no <c>Like</c> to reach for, and
/// no ordering. The legacy surface has no use for it — there, operator sets are inferred from the
/// property's type, and a text property is assumed to accept <c>$like:</c>.
/// </remarks>
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates",
    Justification = "The operators are markers read out of an expression tree, never invoked.")]
public sealed class EqualityField<T> : EconomicFilterField
{
    internal EqualityField()
    {
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => throw NotAnExpression();

    /// <inheritdoc />
    public override int GetHashCode() => throw NotAnExpression();

    /// <summary>Translates to <c>$eq:</c>, or <c>$null:</c> when compared with <see langword="null"/>.</summary>
    public static bool operator ==(EqualityField<T> field, T? value) => throw NotAnExpression();

    /// <summary>Translates to <c>$ne:</c>, or <c>$ne:$null:</c> when compared with <see langword="null"/>.</summary>
    public static bool operator !=(EqualityField<T> field, T? value) => throw NotAnExpression();
}

/// <summary>
/// An ordered property supporting <c>$eq:</c>, <c>$ne:</c>, <c>$lt:</c>, <c>$lte:</c>,
/// <c>$gt:</c> and <c>$gte:</c>.
/// </summary>
/// <typeparam name="T">The value type, e.g. <see cref="DateOnly"/>.</typeparam>
/// <remarks>
/// Deliberately without <c>In</c>: e-conomic restricts <c>$in:</c> and <c>$nin:</c> to numeric
/// values, so only <see cref="NumericField{T}"/> offers them.
/// </remarks>
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates",
    Justification = "The operators are markers read out of an expression tree, never invoked.")]
public sealed class ComparableField<T> : EconomicFilterField
    where T : struct
{
    internal ComparableField()
    {
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => throw NotAnExpression();

    /// <inheritdoc />
    public override int GetHashCode() => throw NotAnExpression();

    /// <summary>Translates to <c>$eq:</c>.</summary>
    public static bool operator ==(ComparableField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$ne:</c>.</summary>
    public static bool operator !=(ComparableField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$lt:</c>.</summary>
    public static bool operator <(ComparableField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$lte:</c>.</summary>
    public static bool operator <=(ComparableField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$gt:</c>.</summary>
    public static bool operator >(ComparableField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$gte:</c>.</summary>
    public static bool operator >=(ComparableField<T> field, T value) => throw NotAnExpression();
}

/// <summary>
/// A numeric property supporting the comparison operators plus <c>$in:</c> and <c>$nin:</c>.
/// </summary>
/// <typeparam name="T">The numeric type.</typeparam>
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates",
    Justification = "The operators are markers read out of an expression tree, never invoked.")]
public sealed class NumericField<T> : EconomicFilterField
    where T : struct
{
    /// <summary>The most values e-conomic accepts in an <c>$in:</c> or <c>$nin:</c> list.</summary>
    public const int MaxInValues = 200;

    internal NumericField()
    {
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => throw NotAnExpression();

    /// <inheritdoc />
    public override int GetHashCode() => throw NotAnExpression();

    /// <summary>Translates to <c>$eq:</c>.</summary>
    public static bool operator ==(NumericField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$ne:</c>.</summary>
    public static bool operator !=(NumericField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$lt:</c>.</summary>
    public static bool operator <(NumericField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$lte:</c>.</summary>
    public static bool operator <=(NumericField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$gt:</c>.</summary>
    public static bool operator >(NumericField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$gte:</c>.</summary>
    public static bool operator >=(NumericField<T> field, T value) => throw NotAnExpression();

    /// <summary>Translates to <c>$in:</c>. At most <see cref="MaxInValues"/> values.</summary>
    /// <param name="values">The values to match.</param>
    /// <returns>Never returns; only meaningful inside a filter expression.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Must be an instance member so it reads as field.In(...) in a lambda.")]
    public bool In(params T[] values) => throw NotAnExpression();

    /// <summary>Translates to <c>$nin:</c>. At most <see cref="MaxInValues"/> values.</summary>
    /// <param name="values">The values to exclude.</param>
    /// <returns>Never returns; only meaningful inside a filter expression.</returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Must be an instance member so it reads as field.NotIn(...) in a lambda.")]
    public bool NotIn(params T[] values) => throw NotAnExpression();
}

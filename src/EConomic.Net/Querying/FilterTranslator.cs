using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace EConomic.Querying;

/// <summary>
/// Translates a filter lambda into e-conomic's <c>filter=</c> syntax.
/// </summary>
/// <remarks>
/// <para>
/// The expression tree is <em>inspected</em>, never compiled. Compiling an expression emits IL at
/// run time, which would break the package's AOT guarantee, so every value is read out of the tree
/// structurally instead.
/// </para>
/// <para>
/// This is not an <c>IQueryable</c> provider on purpose. <c>IQueryable</c> advertises that all of
/// LINQ works, and against an API where most properties are not filterable that promise turns
/// ordinary-looking queries into runtime failures. The filter surface exposes only what the server
/// accepts, so anything that compiles is translatable.
/// </para>
/// </remarks>
public static class FilterTranslator
{
    /// <summary>Translates a filter expression into a <c>filter=</c> value.</summary>
    /// <typeparam name="TFilter">The generated filter surface.</typeparam>
    /// <param name="expression">The filter expression.</param>
    /// <returns>The filter string, without the <c>filter=</c> prefix.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">The expression uses something e-conomic cannot express.</exception>
    public static string Translate<TFilter>(Expression<Func<TFilter, bool>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var builder = new StringBuilder();
        Visit(expression.Body, builder, parenthesise: false);
        return builder.ToString();
    }

    private static void Visit(Expression node, StringBuilder output, bool parenthesise)
    {
        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } binary:
                Combine(binary, "$and:", output, parenthesise);
                return;

            case BinaryExpression { NodeType: ExpressionType.OrElse } binary:
                Combine(binary, "$or:", output, parenthesise);
                return;

            case BinaryExpression binary:
                VisitComparison(binary, output);
                return;

            case MethodCallExpression call:
                VisitCall(call, output);
                return;

            case UnaryExpression { NodeType: ExpressionType.Not } negation:
                VisitNegation(negation, output);
                return;

            default:
                throw Unsupported(node);
        }
    }

    private static void Combine(BinaryExpression binary, string op, StringBuilder output, bool parenthesise)
    {
        // Only group when nested. e-conomic reads a flat chain left to right, so the outermost
        // expression needs no parentheses and adding them everywhere would be noise.
        if (parenthesise)
        {
            output.Append('(');
        }

        Visit(binary.Left, output, parenthesise: true);
        output.Append(op);
        Visit(binary.Right, output, parenthesise: true);

        if (parenthesise)
        {
            output.Append(')');
        }
    }

    private static void VisitComparison(BinaryExpression binary, StringBuilder output)
    {
        var op = binary.NodeType switch
        {
            ExpressionType.Equal => "$eq:",
            ExpressionType.NotEqual => "$ne:",
            ExpressionType.LessThan => "$lt:",
            ExpressionType.LessThanOrEqual => "$lte:",
            ExpressionType.GreaterThan => "$gt:",
            ExpressionType.GreaterThanOrEqual => "$gte:",
            _ => throw Unsupported(binary),
        };

        var field = FieldName(binary.Left) ?? FieldName(binary.Right) ?? throw Unsupported(binary);
        var value = ValueOf(FieldName(binary.Left) is null ? binary.Left : binary.Right);

        // $null: is a value, not an operator: `name$null:` is a syntax error, and the server
        // replies listing the operators it expected. It has to be written `name$eq:$null:`.
        if (value is null)
        {
            if (binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            {
                throw new NotSupportedException(
                    $"Only equality can be used with null; '{binary.NodeType}' cannot.");
            }

            output.Append(field).Append(op).Append(EconomicFilterEscaping.NullValue);
            return;
        }

        output.Append(field).Append(op).Append(EconomicFilterEscaping.Escape(Format(value)));
    }

    private static void VisitCall(MethodCallExpression call, StringBuilder output)
    {
        var field = FieldName(call.Object) ?? throw Unsupported(call);

        switch (call.Method.Name)
        {
            case nameof(TextField.Like):
                var pattern = ValueOf(call.Arguments[0]) as string
                    ?? throw new NotSupportedException("Like requires a non-null pattern.");

                output.Append(field).Append("$like:").Append(EconomicFilterEscaping.EscapePattern(pattern));
                return;

            case "In":
                AppendList(field, "$in:", call, output);
                return;

            case "NotIn":
                AppendList(field, "$nin:", call, output);
                return;

            case nameof(EconomicFilterField.IsNull):
                output.Append(field).Append("$eq:").Append(EconomicFilterEscaping.NullValue);
                return;

            default:
                throw Unsupported(call);
        }
    }

    private static void VisitNegation(UnaryExpression negation, StringBuilder output)
    {
        // Only `!field.IsNull()` has a meaning the server can express; general negation would need
        // De Morgan handling that e-conomic's grammar cannot always represent.
        if (negation.Operand is MethodCallExpression { Method.Name: nameof(EconomicFilterField.IsNull) } call
            && FieldName(call.Object) is { } field)
        {
            output.Append(field).Append("$ne:").Append(EconomicFilterEscaping.NullValue);
            return;
        }

        throw Unsupported(negation);
    }

    private static void AppendList(string field, string op, MethodCallExpression call, StringBuilder output)
    {
        if (ValueOf(call.Arguments[0]) is not System.Collections.IEnumerable values)
        {
            throw new NotSupportedException($"{op} requires a list of values.");
        }

        var formatted = new List<string>();
        foreach (var value in values)
        {
            if (value is null)
            {
                throw new NotSupportedException($"{op} does not accept null values.");
            }

            formatted.Add(EconomicFilterEscaping.Escape(Format(value)));
        }

        if (formatted.Count == 0)
        {
            throw new NotSupportedException($"{op} requires at least one value.");
        }

        if (formatted.Count > NumericField<int>.MaxInValues)
        {
            throw new NotSupportedException(
                $"{op} accepts at most {NumericField<int>.MaxInValues} values; {formatted.Count} were supplied. "
                + "Split the query into batches.");
        }

        output.Append(field).Append(op).Append('[').Append(string.Join(",", formatted)).Append(']');
    }

    /// <summary>The e-conomic field name for a member access on the filter surface, if that is what this is.</summary>
    private static string? FieldName(Expression? node)
    {
        if (node is not MemberExpression { Member: PropertyInfo property } member)
        {
            return null;
        }

        // Must hang off the lambda parameter; a property read on a captured object is a value.
        if (member.Expression is not ParameterExpression)
        {
            return null;
        }

        return property.GetCustomAttribute<EconomicFieldAttribute>()?.Name
            ?? throw new NotSupportedException(
                $"'{property.DeclaringType?.Name}.{property.Name}' has no {nameof(EconomicFieldAttribute)}, "
                + "so the name e-conomic expects is unknown.");
    }

    /// <summary>
    /// Reads a constant out of the tree. Captured variables appear as a field read on a closure
    /// object, which is resolved by reflection rather than by compiling the expression.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Members are read from closure instances the compiler created for this "
            + "expression; they are reachable by construction and cannot be trimmed away.")]
    private static object? ValueOf(Expression node)
    {
        switch (node)
        {
            case ConstantExpression constant:
                return constant.Value;

            case MemberExpression { Expression: null, Member: FieldInfo staticField }:
                return staticField.GetValue(null);

            case MemberExpression { Expression: null, Member: PropertyInfo staticProperty }:
                return staticProperty.GetValue(null);

            case MemberExpression member:
                var target = ValueOf(member.Expression!);
                return member.Member switch
                {
                    FieldInfo field => field.GetValue(target),
                    PropertyInfo property => property.GetValue(target),
                    _ => throw Unsupported(node),
                };

            case NewArrayExpression array:
                var items = new List<object?>(array.Expressions.Count);
                foreach (var element in array.Expressions)
                {
                    items.Add(ValueOf(element));
                }

                return items;

            case UnaryExpression { NodeType: ExpressionType.Convert } convert:
                return ValueOf(convert.Operand);

            default:
                throw Unsupported(node);
        }
    }

    private static string Format(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

        // Timestamps go over in full. e-conomic filters these to the second — verified live — so
        // truncating to a day would quietly widen the query.
        DateTime dateTime => dateTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static NotSupportedException Unsupported(Expression node) =>
        new($"e-conomic cannot express '{node}'. Only the operators the filter surface exposes are supported.");
}

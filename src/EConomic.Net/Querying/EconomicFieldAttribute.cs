namespace EConomic.Querying;

/// <summary>
/// Maps a property on a generated filter or sort surface to the name e-conomic expects.
/// </summary>
/// <remarks>
/// The mapping cannot be a naming convention: nested paths such as
/// <c>customerGroup.customerGroupNumber</c> are legal filter fields and have no C# equivalent, and
/// e-conomic's own casing is not always what a C# property name would produce.
/// </remarks>
/// <param name="name">The field name as e-conomic spells it.</param>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class EconomicFieldAttribute(string name) : Attribute
{
    /// <summary>The field name as e-conomic spells it, e.g. <c>customerGroup.customerGroupNumber</c>.</summary>
    public string Name { get; } = !string.IsNullOrWhiteSpace(name)
        ? name
        : throw new ArgumentException("A field name is required.", nameof(name));
}

using System;

namespace Dreambit;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class HideInInspectorAttribute : Attribute;

/// <summary>Shows an authoring member only when another member has the specified value.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class ShowInInspectorWhenAttribute(string memberName, object value) : Attribute
{
    public string MemberName { get; } = memberName;
    public object Value { get; } = value;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class ReadOnlyInInspectorAttribute : Attribute;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class RangeAttribute(double minimum, double maximum) : Attribute
{
    public double Minimum { get; } = minimum;
    public double Maximum { get; } = maximum;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class HeaderAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class TooltipAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}

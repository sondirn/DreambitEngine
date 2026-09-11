using System.Reflection;

namespace Dreambit.Editor.Inspection;

internal enum InspectorTargetKind
{
    Component,
    Asset
}

internal sealed record InspectorMemberMetadata(
    string SerializedName,
    string DisplayName,
    Type ValueType,
    MemberInfo Member,
    bool CanWrite,
    bool IsReadOnly,
    RangeAttribute? Range,
    string? Header,
    string? Tooltip)
{
    public bool IsVisible(Func<string, object?> getValue)
    {
        var condition = Member.GetCustomAttribute<ShowInInspectorWhenAttribute>();
        return condition is null || Equals(getValue(condition.MemberName), condition.Value);
    }

    public object? GetValue(object target)
    {
        return Member switch
        {
            PropertyInfo property => property.GetValue(target),
            FieldInfo field => field.GetValue(target),
            _ => null
        };
    }

    public void SetValue(object target, object? value)
    {
        switch (Member)
        {
            case PropertyInfo property:
                property.SetValue(target, value);
                break;
            case FieldInfo field:
                field.SetValue(target, value);
                break;
            default:
                throw new NotSupportedException($"Unsupported inspector member '{Member}'.");
        }
    }
}

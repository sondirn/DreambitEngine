namespace Dreambit.EditorApi;

/// <summary>Associates a custom inspector with a Dreambit Component or DreambitAsset type.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DreambitCustomEditorAttribute(Type targetType) : Attribute
{
    public Type TargetType { get; } = targetType ?? throw new ArgumentNullException(nameof(targetType));
    public bool IncludeDerivedTypes { get; init; }
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class HideComponentInEditorAttribute() : Attribute
{
    
}

public enum EditorExtensionLogLevel
{
    Information,
    Warning,
    Error
}

/// <summary>Services available to a game-defined custom Inspector.</summary>
public interface IEditorInspectorContext
{
    object? ActiveTarget { get; }
    IReadOnlyList<object> Targets { get; }
    void DrawDefaultInspector();
    void RecordChange(string name, Action mutation);
    void Log(EditorExtensionLogLevel level, string message, Exception? exception = null);
}

/// <summary>Implemented by game-side custom Component or asset Inspectors.</summary>
public interface IDreambitCustomEditor
{
    void Draw(IEditorInspectorContext context);
}

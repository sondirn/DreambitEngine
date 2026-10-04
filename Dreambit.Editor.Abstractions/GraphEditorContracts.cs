using System.Numerics;
namespace Dreambit.EditorApi;

public sealed record EditorGraphPort(string Id, string Label, string Target);
public sealed record EditorGraphNode(string Id, string Title, string Subtitle, Vector2 Position,
    IReadOnlyList<EditorGraphPort> Outputs, bool IsEntry = false);
/// <summary>Optional graph view of a normal asset. Does not replace its inspector.</summary>
public interface IEditorGraphAsset
{
    IReadOnlyList<string> NodeKinds { get; }
    IReadOnlyList<EditorGraphNode> GetNodes();
    string AddNode(string kind, Vector2 position);
    void RemoveNode(string id);
    void MoveNode(string id, Vector2 position);
    void Connect(string node, string port, string target);
    void DrawNodeDetails(string id, IEditorGraphContext context);
    IReadOnlyList<string> ValidateGraph();
    IReadOnlyList<string> ValidateGraph(IEditorGraphContext context) => ValidateGraph();
    void DrawPreview(IEditorGraphContext context);
}
public interface IEditorGraphContext
{
    void RecordChange(string name, Action mutation);
    void DrawFields(object value, params string[] members);
    DreambitAsset LoadAsset(AssetId id, Type type);
    IReadOnlyList<DreambitAsset> LoadAssets(Type type);
}

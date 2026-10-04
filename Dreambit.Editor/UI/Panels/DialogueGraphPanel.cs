using System.Numerics;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Inspection;
using Dreambit.EditorApi;
using ImGuiNET;
namespace Dreambit.Editor.UI.Panels;

/// <summary>Generic asset graph surface, independent of the regular inspector.</summary>
internal sealed class DialogueGraphPanel : EditorPanel
{
    private readonly AssetEditingService _editing;
    private readonly AssetDatabase _assets;
    private readonly InspectorMetadataCache _metadata;
    private readonly InspectorValueDrawerRegistry _drawers;
    private AssetId _assetId;
    private string _selected = "", _linkNode = "", _linkPort = "", _search = "";
    private Vector2 _pan = new(40, 60);
    private float _zoom = 1;
    private string _error = "";
    private long _validatedRevision = -1, _validatedCatalog = -1;
    private IReadOnlyList<string> _validationErrors = [];
    public DialogueGraphPanel(AssetEditingService editing, AssetDatabase assets, InspectorMetadataCache metadata,
        EditorDragDropService dragDrop) : base(EditorPanelIds.DialogueGraph, "Dialogue Graph")
    {
        _editing = editing; _assets = assets; _metadata = metadata;
        _drawers = new InspectorValueDrawerRegistry(assets, dragDrop, () => null);
    }
    protected override void DrawContents()
    {
        if (_editing.Current is not { Instance: IEditorGraphAsset } document)
        { ImGui.TextWrapped("Select a dialogue asset in Project to edit its graph. The Inspector remains available for asset properties."); return; }
        if (_assetId != document.Asset.Id)
        { _validatedRevision = -1; _assetId = document.Asset.Id; _selected = _linkNode = _linkPort = ""; _pan = new(40, 60); _zoom = 1; }
        try
        {
            DrawGraph(document);
            _error = "";
        }
        catch (Exception e) { _error = e.Message; }
        if (_error.Length > 0) ImGui.TextWrapped(_error);
        if (!ImGui.IsAnyItemActive()) document.Undo.EndMergeGroup();
    }
    private void DrawGraph(DreambitAssetDocument document)
    {
        var graph = (IEditorGraphAsset)document.Instance;
        ImGui.TextUnformatted(document.Asset.Name + (document.IsDirty ? " *" : "")); ImGui.SameLine();
        if (ImGui.Button("Save")) _editing.Save(); ImGui.SameLine();
        if (ImGui.Button("Undo")) { document.Undo.Undo(); return; }
        ImGui.SameLine();
        if (ImGui.Button("Redo")) { document.Undo.Redo(); return; }
        ImGui.SameLine();
        if (ImGui.Button("Center")) { _pan = new(40, 60); _zoom = 1; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("Add node", "Choose type"))
        {
            try
            {
                foreach (var kind in graph.NodeKinds)
                    if (ImGui.Selectable(kind)) document.Apply("Add " + kind, a => _selected = ((IEditorGraphAsset)a).AddNode(kind, (new Vector2(80, 80) - _pan) / _zoom));
            }
            finally { ImGui.EndCombo(); }
        }
        ImGui.TextDisabled("Drag nodes | Middle-drag to pan | Wheel to zoom | Click output, then input to connect");
        var width = Math.Max(240, ImGui.GetContentRegionAvail().X - 330);
        ImGui.BeginChild("canvas", new Vector2(width, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try { DrawCanvas(document, graph); } finally { ImGui.EndChild(); }
        ImGui.SameLine();
        ImGui.BeginChild("details", Vector2.Zero, ImGuiChildFlags.Borders);
        try
        {
            var context = new GraphContext(document, _assets, _metadata, _drawers);
            ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##search", "Find a node", ref _search, 200);
            if (_search.Length > 0)
                foreach (var n in graph.GetNodes().Where(n => n.Title.Contains(_search, StringComparison.OrdinalIgnoreCase)))
                    if (ImGui.Selectable(n.Title + "##find" + n.Id)) { _selected = n.Id; _pan = new Vector2(60, 60) - n.Position * _zoom; }
            if (_selected.Length > 0)
            {
                if (ImGui.Button("Delete selected node"))
                { document.Apply("Delete node", a => ((IEditorGraphAsset)a).RemoveNode(_selected)); _selected = ""; return; }
                graph.DrawNodeDetails(_selected, context);
            }
            if (ImGui.CollapsingHeader("Validation", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var catalogVersion = _assets?.GetSnapshot().Version ?? 0;
                if (_validatedRevision != document.Revision || _validatedCatalog != catalogVersion)
                {
                    _validationErrors = graph.ValidateGraph(context);
                    _validatedRevision = document.Revision; _validatedCatalog = catalogVersion;
                }
                var errors = _validationErrors;
                if (errors.Count == 0) ImGui.TextUnformatted("Graph connections are valid.");
                foreach (var error in errors) ImGui.TextWrapped(error);
            }
            if (ImGui.CollapsingHeader("Conversation preview")) graph.DrawPreview(context);
        }
        finally { ImGui.EndChild(); }
    }
    private void DrawCanvas(DreambitAssetDocument document, IEditorGraphAsset graph)
    {
        var origin = ImGui.GetCursorScreenPos();
        var area = ImGui.GetContentRegionAvail();
        var draw = ImGui.GetWindowDrawList();
        var nodes = graph.GetNodes();
        Vector2 Point(Vector2 p) => origin + _pan + p * _zoom;
        var grid = 32 * _zoom;
        for (var x = _pan.X % grid; x < area.X; x += grid) draw.AddLine(origin + new Vector2(x, 0), origin + new Vector2(x, area.Y), 0x223f3f3f);
        for (var y = _pan.Y % grid; y < area.Y; y += grid) draw.AddLine(origin + new Vector2(0, y), origin + new Vector2(area.X, y), 0x223f3f3f);
        foreach (var n in nodes)
            for (var i = 0; i < n.Outputs.Count; i++)
            {
                var port = n.Outputs[i]; var target = nodes.FirstOrDefault(t => t.Id == port.Target);
                if (target is null) continue;
                var from = Point(n.Position + new Vector2(230, 65 + i * 25));
                var to = Point(target.Position + new Vector2(0, 18));
                draw.AddBezierCubic(from, from + new Vector2(80, 0), to - new Vector2(80, 0), to, 0xffb9a36c, 2);
            }
        foreach (var n in nodes)
        {
            var pos = Point(n.Position); var size = new Vector2(230, 55 + Math.Max(1, n.Outputs.Count) * 25) * _zoom;
            draw.AddRectFilled(pos, pos + size, _selected == n.Id ? 0xff584333 : 0xff342b25, 6);
            draw.AddRect(pos, pos + size, n.IsEntry ? 0xff6ed7a0 : 0xff847666, 6);
            draw.PushClipRect(pos, pos + size, true);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * _zoom, pos + new Vector2(12, 8) * _zoom, 0xffeeeeee, n.Title);
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * _zoom, pos + new Vector2(12, 32) * _zoom, 0xffb8b8b8, n.Subtitle);
            draw.PopClipRect();
            ImGui.SetCursorScreenPos(pos + new Vector2(8, 4));
            ImGui.InvisibleButton("node" + n.Id, new Vector2(size.X - 16, 42 * _zoom));
            if (ImGui.IsItemClicked()) _selected = n.Id;
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                var moved = n.Position + ImGui.GetIO().MouseDelta / _zoom;
                document.Apply("Move node", a => ((IEditorGraphAsset)a).MoveNode(n.Id, moved), "graph.move." + n.Id);
            }
            var input = Point(n.Position + new Vector2(0, 18));
            draw.AddCircleFilled(input, 6, 0xff88d0e0);
            ImGui.SetCursorScreenPos(input - new Vector2(8)); ImGui.InvisibleButton("in" + n.Id, new Vector2(16));
            if (ImGui.IsItemClicked() && _linkNode.Length > 0)
            {
                document.Apply("Connect nodes", a => ((IEditorGraphAsset)a).Connect(_linkNode, _linkPort, n.Id));
                _linkNode = _linkPort = "";
            }
            for (var i = 0; i < n.Outputs.Count; i++)
            {
                var port = n.Outputs[i]; var p = Point(n.Position + new Vector2(230, 65 + i * 25));
                draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * _zoom, p - new Vector2(215, 8) * _zoom, 0xffeeeeee, port.Label.Length > 26 ? port.Label[..26] + "…" : port.Label);
                draw.AddCircleFilled(p, 6, 0xffa4cb83);
                ImGui.SetCursorScreenPos(p - new Vector2(8)); ImGui.InvisibleButton("out" + n.Id + port.Id, new Vector2(16));
                if (ImGui.IsItemClicked()) { _linkNode = n.Id; _linkPort = port.Id; }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) document.Apply("Disconnect", a => ((IEditorGraphAsset)a).Connect(n.Id, port.Id, ""));
            }
        }
        if (_linkNode.Length > 0)
        {
            var n = nodes.FirstOrDefault(n => n.Id == _linkNode);
            var i = n is null ? -1 : n.Outputs.ToList().FindIndex(p => p.Id == _linkPort);
            if (i >= 0) draw.AddLine(Point(n!.Position + new Vector2(230, 65 + i * 25)), ImGui.GetMousePos(), 0xffa4cb83, 2);
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) _linkNode = _linkPort = "";
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(area);
        if (ImGui.IsWindowHovered())
        {
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle)) _pan += ImGui.GetIO().MouseDelta;
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0)
            {
                var mouse = ImGui.GetMousePos() - origin; var world = (mouse - _pan) / _zoom;
                _zoom = Math.Clamp(_zoom + wheel * .1f, .4f, 1.6f); _pan = mouse - world * _zoom;
            }
        }
    }
    private sealed class GraphContext(DreambitAssetDocument document, AssetDatabase assets,
        InspectorMetadataCache metadata, InspectorValueDrawerRegistry drawers) : IEditorGraphContext
    {
        public void RecordChange(string name, Action mutation) => document.Apply(name, _ => mutation(),
            ImGui.IsAnyItemActive() ? "Graph." + ImGui.GetID(name) : null);
        public void DrawFields(object value, params string[] members)
        {
            foreach (var member in metadata.Get(value.GetType(), InspectorTargetKind.Asset))
            {
                if (members.Length > 0 && !members.Contains(member.SerializedName)) continue;
                var result = drawers.Draw(member.DisplayName, member.ValueType, member.GetValue(value),
                    new InspectorValueDrawContext("Graph." + member.SerializedName, member, false, member.IsReadOnly));
                if (result.Changed && !member.IsReadOnly)
                    document.Apply("Edit " + member.DisplayName, _ => member.SetValue(value, result.Value), "Graph." + member.SerializedName);
            }
        }
        public DreambitAsset LoadAsset(AssetId id, Type type)
        {
            if (id == document.Asset.Id && type.IsInstanceOfType(document.Instance)) return document.Instance;
            var record = assets.GetSnapshot().Assets.FirstOrDefault(a => a.Id == id) ?? throw new InvalidDataException("Missing asset " + id);
            var result = DreambitJson.Deserialize(File.ReadAllText(Path.Combine(assets.ContentRoot, record.RelativePath)), type) as DreambitAsset
                ?? throw new InvalidDataException("Invalid asset " + record.RelativePath);
            result.AssetId = record.Id;
            return result;
        }
        public IReadOnlyList<DreambitAsset> LoadAssets(Type type) => assets.GetSnapshot().Assets
            .Where(a => a.TypeId == DreambitAssetTypeRegistry.GetTypeId(type)).Select(a => LoadAsset(a.Id, type)).ToArray();
    }
}

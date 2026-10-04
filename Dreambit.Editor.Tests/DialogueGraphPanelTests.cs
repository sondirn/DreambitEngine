using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dreambit.ECS;
using Dreambit.Editor.Assets;
using Dreambit.Editor.Inspection;
using Dreambit.Editor.UI;
using Dreambit.Editor.UI.Panels;
using Dreambit.EditorApi;
using ImGuiNET;
using Newtonsoft.Json;

namespace Dreambit.Editor.Tests;

public sealed class DialogueGraphPanelTests
{
    [Fact]
    public void CanvasConnectDragDisconnectAndUndoOperateOnTheSameAssetDocument()
    {
        using var fixture = new Fixture();
        var p = fixture.NodeOrigin();
        fixture.Click(p + new Vector2(100, 20));
        Assert.Equal("greeting", fixture.GetField<string>("_selected"));
        // The output and input are independent of the node's drag surface.
        fixture.Click(p + new Vector2(230, 65));
        fixture.Click(p + new Vector2(400, 118));
        Assert.Equal("end", fixture.Graph.Nodes[0].Next);
        fixture.Document.Undo.Undo();
        Assert.Equal("", fixture.Graph.Nodes[0].Next);
        fixture.Document.Undo.Redo();
        Assert.Equal("end", fixture.Graph.Nodes[0].Next);
        fixture.Click(p + new Vector2(230, 65), 1);
        Assert.Equal("", fixture.Graph.Nodes[0].Next);
        fixture.Frame(p + new Vector2(100, 20));
        fixture.Frame(p + new Vector2(100, 20), true);
        fixture.Frame(p + new Vector2(140, 50), true);
        fixture.Frame(p + new Vector2(140, 50));
        Assert.Equal(40, fixture.Graph.Nodes[0].X);
        Assert.Equal(30, fixture.Graph.Nodes[0].Y);
        fixture.Document.Undo.Undo();
        Assert.Equal(0, fixture.Graph.Nodes[0].X);
        Assert.True(fixture.Document.IsDirty || fixture.Document.Undo.CanRedo);
        fixture.Document.Save(fixture.Path);
        var roundTrip = DreambitJson.Deserialize<TestGraphAsset>(File.ReadAllText(fixture.Path))!;
        Assert.Equal(fixture.Graph.Nodes[0].Next, roundTrip.Nodes[0].Next);
        Assert.Equal(fixture.Graph.Nodes.Count, roundTrip.Nodes.Count);
    }

    [Fact]
    public void CanvasZoomAndPanDoNotChangeSerializedGraphAndInspectorMetadataRemainsAvailable()
    {
        using var fixture = new Fixture();
        var before = fixture.Document.CaptureJson();
        var position = fixture.NodeOrigin() + new Vector2(280, 220);
        fixture.Frame(position);
        ImGui.GetIO().AddMouseWheelEvent(0, 1);
        fixture.Frame(position);
        Assert.True(fixture.GetField<float>("_zoom") > 1);
        var oldPan = fixture.GetField<Vector2>("_pan");
        fixture.Frame(position, true, 2); fixture.Frame(position + new Vector2(20, 15), true, 2); fixture.Frame(position + new Vector2(20, 15));
        Assert.NotEqual(oldPan, fixture.GetField<Vector2>("_pan"));
        Assert.Equal(before, fixture.Document.CaptureJson());
        Assert.Contains(new InspectorMetadataCache().Get(typeof(TestGraphAsset), InspectorTargetKind.Asset), m => m.SerializedName == "Nodes");
        Assert.DoesNotContain(typeof(TestGraphAsset).GetCustomAttributes(), a => a is DreambitCustomEditorAttribute);
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "graph-" + Guid.NewGuid() + ".asset");
        public DreambitAssetDocument Document { get; }
        public TestGraphAsset Graph => (TestGraphAsset)Document.Instance;
        private readonly DialogueGraphPanel _panel;
        private readonly nint _previous = ImGui.GetCurrentContext();
        private readonly nint _gui;
        public Fixture()
        {
            File.WriteAllText(Path, DreambitJson.Serialize(new TestGraphAsset()));
            var info = new FileInfo(Path);
            var record = new AssetRecord(AssetId.New(), info.Name, info.Name, "", info.Name, AssetKind.DreambitAsset, "test.dialogue-graph", info.Length, info.LastWriteTimeUtc);
            var metadata = new InspectorMetadataCache();
            Document = DreambitAssetDocument.Open(record, Path, typeof(TestGraphAsset), metadata);
            var editing = (AssetEditingService)RuntimeHelpers.GetUninitializedObject(typeof(AssetEditingService));
            typeof(AssetEditingService).GetProperty("Current")!.SetValue(editing, Document);
            _panel = new DialogueGraphPanel(editing, null!, metadata, new EditorDragDropService());
            _gui = ImGui.CreateContext();
            var io = ImGui.GetIO(); io.DisplaySize = new(1280, 800); io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault(); io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out _, out _);
            Frame(new(-100, -100)); Frame(new(-100, -100));
        }
        public void Frame(Vector2 position, bool down = false, int button = 0)
        {
            var io = ImGui.GetIO(); io.AddMousePosEvent(position.X, position.Y);
            for (var i = 0; i < 3; i++) io.AddMouseButtonEvent(i, down && i == button);
            ImGui.NewFrame(); ImGui.SetNextWindowPos(new(10, 10)); ImGui.SetNextWindowSize(new(1200, 750));
            _panel.Draw(); ImGui.Render();
            Assert.Equal("", GetField<string>("_error"));
        }
        public T GetField<T>(string field) => (T)typeof(DialogueGraphPanel).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_panel)!;
        public Vector2 NodeOrigin()
        {
            var min = new Vector2(float.MaxValue);
            var data = ImGui.GetDrawData();
            for (var list = 0; list < data.CmdListsCount; list++)
                for (var i = 0; i < data.CmdLists[list].VtxBuffer.Size; i++)
                {
                    var v = data.CmdLists[list].VtxBuffer[i];
                    if (v.col == 0xff342b25) min = Vector2.Min(min, v.pos);
                }
            Assert.True(float.IsFinite(min.X) && min.X < float.MaxValue);
            return min;
        }
        public void Click(Vector2 p, int button = 0) { Frame(p); Frame(p, true, button); Frame(p); }
        public void Dispose()
        {
            _panel.Dispose(); Document.Dispose(); File.Delete(Path);
            ImGui.DestroyContext(_gui); ImGui.SetCurrentContext(_previous);
        }
    }

    [DreambitAssetType("test.dialogue-graph")]
    public sealed class TestGraphAsset : DreambitAsset, IEditorGraphAsset
    {
        [DreambitSerialize, JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<Node> Nodes { get; set; } = [new() { Id = "greeting", Title = "Greeting" }, new() { Id = "end", Title = "End", X = 400, Y = 100 }];
        public IReadOnlyList<string> NodeKinds => ["Speech"];
        public IReadOnlyList<EditorGraphNode> GetNodes() => Nodes.Select(n => new EditorGraphNode(n.Id, n.Title, "Speech", new(n.X, n.Y), [new("next", "Next", n.Next)])).ToArray();
        public string AddNode(string kind, Vector2 position) { var n = new Node { X = position.X, Y = position.Y }; Nodes.Add(n); return n.Id; }
        public void RemoveNode(string id) => Nodes.RemoveAll(n => n.Id == id);
        public void MoveNode(string id, Vector2 p) { var n = Nodes.First(n => n.Id == id); n.X = p.X; n.Y = p.Y; }
        public void Connect(string id, string port, string target) => Nodes.First(n => n.Id == id).Next = target;
        public void DrawNodeDetails(string id, IEditorGraphContext context) => context.DrawFields(Nodes.First(n => n.Id == id), "Title");
        public IReadOnlyList<string> ValidateGraph() => [];
        public void DrawPreview(IEditorGraphContext context) => ImGui.TextUnformatted("Preview");
    }
    public sealed class Node
    {
        [DreambitSerialize] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DreambitSerialize] public string Title { get; set; } = "Speech";
        [DreambitSerialize] public string Next { get; set; } = "";
        [DreambitSerialize] public float X { get; set; }
        [DreambitSerialize] public float Y { get; set; }
    }
}

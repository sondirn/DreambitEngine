using Dreambit.ECS;
using Dreambit.Editor.Inspection;
using Dreambit.EditorApi;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;

namespace Dreambit.Editor.Tests;

public sealed class Box2DInspectorTests
{
    [Fact]
    public void NullBlueprintBoundsCanBeCreatedThroughTheRegisteredInspectorDrawer()
    {
        var previous = ImGui.GetCurrentContext();
        var gui = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(800, 600);
            io.DeltaTime = 1f / 60f;
            io.Fonts.AddFontDefault();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out _, out _);

            var metadata = Assert.Single(new InspectorMetadataCache().Get(
                typeof(BoxOwner), InspectorTargetKind.Component));
            var registry = new InspectorValueDrawerRegistry();
            var context = new InspectorValueDrawContext("Bounds", metadata, false, false);
            object? bounds = null;
            Vector2 buttonPosition = default;

            InspectorValueDrawResult Frame(bool mouseDown, bool readOnly = false)
            {
                io.AddMousePosEvent(buttonPosition.X, buttonPosition.Y);
                io.AddMouseButtonEvent(0, mouseDown);
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new Vector2(10, 10));
                ImGui.SetNextWindowSize(new Vector2(600, 400));
                ImGui.Begin("Blueprint", ImGuiWindowFlags.NoSavedSettings);
                var result = registry.Draw("Bounds", typeof(Box2D), bounds, context with { ReadOnly = readOnly });
                // The last item is the whole property row; Create sits just after its label column.
                var rowMin = ImGui.GetItemRectMin();
                buttonPosition = new Vector2(rowMin.X + EditorGuiTheme.PropertyLabelWidth + 20, rowMin.Y + 10);
                ImGui.End();
                ImGui.Render();
                return result;
            }

            Frame(false);
            Frame(false); // Establish hover before pressing.
            Frame(true);
            var created = Frame(false);
            Assert.True(created.Changed);
            var box = Assert.IsType<Box2D>(created.Value);
            Assert.Equal((Vector2.Zero, Vector2.One), Box2DValueDrawer.GetDimensions(box));
            bounds = box;
            var existing = Frame(false);
            Assert.False(existing.Changed);
            Assert.Same(box, existing.Value);

            bounds = null;
            Frame(false, readOnly: true);
            Frame(true, readOnly: true);
            Assert.False(Frame(false, readOnly: true).Changed);
        }
        finally
        {
            ImGui.DestroyContext(gui);
            ImGui.SetCurrentContext(previous);
        }
    }

    [Fact]
    public void EditedBoxDimensionsRoundTripThroughBlueprintSerialization()
    {
        var original = Box2DValueDrawer.CreateBox(Vector2.Zero, Vector2.One);
        var edited = Box2DValueDrawer.CreateBox(new Vector2(2, -3), new Vector2(8, 2));
        var blueprint = new ComponentBlueprint { Properties = { ["Bounds"] = DreambitJson.ToToken(edited) } };
        var restored = DreambitJson.Deserialize<ComponentBlueprint>(DreambitJson.Serialize(blueprint));
        var box = Assert.IsType<Box2D>(DreambitJson.FromToken(restored.Properties["Bounds"], typeof(Box2D)));
        Assert.Equal((new Vector2(2, -3), new Vector2(8, 2)), Box2DValueDrawer.GetDimensions(box));
        Assert.Equal((Vector2.Zero, Vector2.One), Box2DValueDrawer.GetDimensions(original));
        Assert.Equal(new Microsoft.Xna.Framework.Vector2(-2, -4), box.TopLeft);
        Assert.Equal(new Microsoft.Xna.Framework.Vector2(6, -2), box.BottomRight);
    }

    private sealed class BoxOwner : Component
    {
        [DreambitSerialize] public Box2D? Bounds { get; set; }
    }
}

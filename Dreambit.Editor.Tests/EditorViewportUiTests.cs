using Dreambit.Editor.Persistence;
using Dreambit.Editor.UI;

namespace Dreambit.Editor.Tests;

public sealed class EditorViewportUiTests
{
    [Fact]
    public void WorkspaceDefaultsToOneWorldUnitGrid()
    {
        var workspace = new EditorWorkspaceState();

        Assert.Equal(1f, workspace.GridSize);
    }

    [Fact]
    public void ZoomWheelHasNoArtificialOneHundredTimesCeiling()
    {
        var zoom = EditorViewportUi.ApplyZoomWheel(100f, 1f);

        Assert.True(zoom > 100f);
        Assert.True(float.IsFinite(zoom));
    }

    [Theory]
    [InlineData(0f, 0.00001f)]
    [InlineData(-4f, 0.00001f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(2.5f, 2.5f)]
    public void GridSizeIsAlwaysUsable(float input, float expected)
    {
        Assert.Equal(expected, EditorViewportUi.NormalizeGridSize(input));
    }
}

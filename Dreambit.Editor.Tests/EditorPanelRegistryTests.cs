using Dreambit.Editor.Persistence;
using Dreambit.Editor.UI.Panels;

namespace Dreambit.Editor.Tests;

public sealed class EditorPanelRegistryTests
{
    [Fact]
    public void AppliesPersistedVisibilityWhenRegistering()
    {
        var state = new EditorWorkspaceState
        {
            PanelVisibility = new Dictionary<string, bool>
            {
                ["test"] = false
            }
        };
        using var registry = new EditorPanelRegistry(state);
        var panel = new TestPanel("test");

        registry.Register(panel);

        Assert.False(panel.IsOpen);
    }

    [Fact]
    public void RejectsDuplicatePanelIds()
    {
        using var registry = new EditorPanelRegistry(new EditorWorkspaceState());
        registry.Register(new TestPanel("duplicate"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => registry.Register(new TestPanel("duplicate")));

        Assert.Contains("already registered", exception.Message);
    }

    [Fact]
    public void DisposesRegisteredPanelsInReverseOrder()
    {
        var disposeOrder = new List<string>();
        var registry = new EditorPanelRegistry(new EditorWorkspaceState());
        registry.Register(new TestPanel("first", disposeOrder));
        registry.Register(new TestPanel("second", disposeOrder));

        registry.Dispose();

        Assert.Equal(["second", "first"], disposeOrder);
    }

    [Fact]
    public void ContinuesDisposingPanelsAfterOneFails()
    {
        var disposeOrder = new List<string>();
        var registry = new EditorPanelRegistry(new EditorWorkspaceState());
        registry.Register(new TestPanel("first", disposeOrder));
        registry.Register(new TestPanel("failing", disposeOrder, throwOnDispose: true));
        registry.Register(new TestPanel("last", disposeOrder));

        var exception = Assert.Throws<AggregateException>(registry.Dispose);

        Assert.Equal(["last", "failing", "first"], disposeOrder);
        Assert.Single(exception.InnerExceptions);
    }

    [Fact]
    public void CapturesCurrentPanelVisibilityBeforeWorkspacePersistence()
    {
        var state = new EditorWorkspaceState();
        using var registry = new EditorPanelRegistry(state);
        var panel = new TestPanel("visibility") { IsOpen = false };
        registry.Register(panel);

        panel.IsOpen = true;
        registry.CaptureVisibility();

        Assert.True(state.PanelVisibility[panel.Id]);
    }

    private sealed class TestPanel : IEditorPanel
    {
        private readonly List<string>? _disposeOrder;

        private readonly bool _throwOnDispose;

        public TestPanel(
            string id,
            List<string>? disposeOrder = null,
            bool throwOnDispose = false)
        {
            Id = id;
            Title = id;
            WindowName = id;
            _disposeOrder = disposeOrder;
            _throwOnDispose = throwOnDispose;
        }

        public string Id { get; }
        public string Title { get; }
        public string WindowName { get; }
        public bool IsOpen { get; set; } = true;

        public void Draw()
        {
        }

        public void Dispose()
        {
            _disposeOrder?.Add(Id);
            if (_throwOnDispose)
                throw new InvalidOperationException("Dispose failed for testing.");
        }
    }
}

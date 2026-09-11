using Dreambit.Editor.UI;

namespace Dreambit.Editor.Tests;

public sealed class EditorApplicationShortcutTests
{
    [Fact]
    public void DocumentShortcutsAreAllowedWithoutTextInputFocus()
    {
        Assert.True(EditorShortcutHandler.ShouldHandleDocumentShortcut(wantTextInput: false));
    }

    [Fact]
    public void DocumentShortcutsAreSuppressedWhileImGuiOwnsTextInput()
    {
        Assert.False(EditorShortcutHandler.ShouldHandleDocumentShortcut(wantTextInput: true));
    }
}

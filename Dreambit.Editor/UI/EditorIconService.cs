using System.Numerics;
using System.Text.Json;
using Dreambit.Editor.Graphics;
using Dreambit.EditorApi;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;

namespace Dreambit.Editor.UI;

internal sealed class EditorIconService : IDisposable
{
    private readonly ImGuiRenderer _imGui;
    private readonly Dictionary<string, IconUv> _icons = new(StringComparer.OrdinalIgnoreCase);
    private Texture2D? _atlas;
    private nint _textureId;
    private bool _disposed;

    public EditorIconService(GraphicsDevice graphicsDevice, ImGuiRenderer imGui)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        _imGui = imGui;

        var iconDirectory = Path.Combine(AppContext.BaseDirectory, "Icons");
        var manifestPath = Path.Combine(iconDirectory, "atlas_32_manifest.json");
        var atlasPath = Path.Combine(iconDirectory, "atlas_32.png");
        if (!File.Exists(manifestPath) || !File.Exists(atlasPath))
            return;

        try
        {
            var manifest = JsonSerializer.Deserialize<IconManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest?.Icons is null)
                return;

            foreach (var icon in manifest.Icons)
            {
                if (!string.IsNullOrWhiteSpace(icon.Icon))
                    _icons[icon.Icon] = new IconUv(
                        new Vector2(icon.U0, icon.V0),
                        new Vector2(icon.U1, icon.V1));
            }

            using var stream = File.OpenRead(atlasPath);
            _atlas = Texture2D.FromStream(graphicsDevice, stream);
            _atlas.Name = "Dreambit Editor icon atlas";
            _textureId = _imGui.BindTexture(_atlas);
        }
        catch
        {
            _icons.Clear();
            _atlas?.Dispose();
            _atlas = null;
            _textureId = 0;
        }
    }

    public bool IsAvailable => _textureId != 0;
    
    public bool HasIcon(string icon) => 
        !string.IsNullOrWhiteSpace(icon) && TryGet(icon, out _);

    public bool Button(
        string id,
        string icon,
        string tooltip,
        bool active = false,
        float size = EditorGuiTheme.ToolbarIconButtonSize)
    {
        using var idScope = EditorGui.PushId(id);
        if (active)
            ImGui.PushStyleColor(ImGuiCol.Button, EditorGuiTheme.SelectedBackground);
        bool clicked;
        try
        {
            clicked = ImGui.Button("##IconButton", new Vector2(size, size));
        }
        finally
        {
            if (active)
                ImGui.PopStyleColor();
        }

        if (TryGet(icon, out var uv))
        {
            var minimum = ImGui.GetItemRectMin();
            var maximum = ImGui.GetItemRectMax();
            var padding = MathF.Max(3f, size * 0.18f);
            ImGui.GetWindowDrawList().AddImage(
                _textureId,
                minimum + new Vector2(padding),
                maximum - new Vector2(padding),
                uv.Minimum,
                uv.Maximum);
        }
        else
        {
            var minimum = ImGui.GetItemRectMin();
            var textSize = ImGui.CalcTextSize("?");
            ImGui.GetWindowDrawList().AddText(
                minimum + new Vector2((size - textSize.X) * 0.5f, (size - textSize.Y) * 0.5f),
                ImGui.GetColorU32(ImGuiCol.Text),
                "?");
        }

        EditorGui.ShowTooltip(tooltip);
        return clicked;
    }

    public void DrawAt(
        ImDrawListPtr drawList,
        string icon,
        Vector2 minimum,
        Vector2 size,
        Vector4? tint = null)
    {
        if (!TryGet(icon, out var uv))
            return;
        drawList.AddImage(
            _textureId,
            minimum,
            minimum + size,
            uv.Minimum,
            uv.Maximum,
            ImGui.GetColorU32(tint ?? Vector4.One));
    }

    private bool TryGet(string icon, out IconUv uv)
    {
        if (_textureId != 0 && _icons.TryGetValue(icon, out uv))
            return true;
        uv = default;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_textureId != 0)
            _imGui.UnbindTexture(_textureId);
        _atlas?.Dispose();
        _atlas = null;
        _textureId = 0;
        _icons.Clear();
        _disposed = true;
    }

    private sealed class IconManifest
    {
        public List<IconEntry>? Icons { get; set; }
    }

    private sealed class IconEntry
    {
        public string Icon { get; set; } = string.Empty;
        public float U0 { get; set; }
        public float V0 { get; set; }
        public float U1 { get; set; }
        public float V1 { get; set; }
    }

    private readonly record struct IconUv(Vector2 Minimum, Vector2 Maximum);
}

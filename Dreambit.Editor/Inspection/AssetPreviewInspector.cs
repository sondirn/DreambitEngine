using Dreambit.Editor.Assets;
using Dreambit.Editor.Graphics;
using Dreambit.EditorApi;
using ImGuiNET;

namespace Dreambit.Editor.Inspection;

internal sealed class AssetPreviewInspector(AssetPreviewService previews) : ISourceAssetInspector
{
    public bool CanInspect(AssetRecord asset) => true;

    public void Draw(AssetRecord asset)
    {
        EditorGui.Header(asset.Name, asset.RelativePath);
        EditorGui.Space();
        DrawBody(asset);
    }

    public void DrawBody(AssetRecord asset)
    {
        try
        {
            if (previews.TryGetTexture(asset, out var texture, out var width, out var height))
            {
                var available = ImGui.GetContentRegionAvail();
                var previewWidth = MathF.Min(available.X, width);
                var previewHeight = previewWidth * height / MathF.Max(1, width);
                if (previewHeight > available.Y)
                {
                    previewHeight = available.Y;
                    previewWidth = previewHeight * width / MathF.Max(1, height);
                }

                ImGui.Image(texture, new System.Numerics.Vector2(previewWidth, previewHeight));
                EditorGui.MutedText($"{width} × {height}");
                return;
            }
        }
        catch (Exception exception)
        {
            EditorGui.Error(exception.Message);
        }

        EditorGui.WrappedText(
            asset.Kind == AssetKind.Scene
                ? "Double-click this scene to open it."
                : "No loaded Dreambit asset type is available for this file. Its data remains untouched.");
    }
}

using Dreambit.Editor.Assets;
using Dreambit.EditorApi;
using DreambitEngine.AssetBaker.Abstractions;

namespace Dreambit.Editor.Inspection;

internal sealed class TextureSourceAssetInspector(
    AssetDatabase assets,
    AssetEditingService assetEditing,
    AssetPreviewInspector preview) : ISourceAssetInspector
{
    private static readonly TextureSemantic[] SemanticChoices =
        [TextureSemantic.Color, TextureSemantic.NormalMap];

    private string? _saveError;
    private AssetId _lastAssetId;

    public bool CanInspect(AssetRecord asset) => asset.Kind == AssetKind.Texture;

    public void Draw(AssetRecord asset)
    {
        if (_lastAssetId != asset.Id)
        {
            _lastAssetId = asset.Id;
            _saveError = null;
        }

        EditorGui.Header(asset.Name, asset.RelativePath);
        EditorGui.Space();

        var semantic = asset.ImportSettings?.Texture?.Semantic ?? TextureSemantic.Color;
        using (var section = EditorGui.Section(
                   "Texture.ImportSettings",
                   "Import Settings",
                   description: "Controls how this image is prepared for rendering."))
        {
            if (section.IsOpen && EditorGui.EnumProperty(
                    "Texture.Semantic",
                    "Texture Type",
                    ref semantic,
                    SemanticChoices,
                    DisplaySemantic,
                    tooltip: "Normal maps are baked as linear data without alpha premultiplication."))
            {
                if (assets.TrySetTextureSemantic(asset.Id, semantic, out var error))
                {
                    _saveError = null;
                    assetEditing.RefreshFromDatabase();
                }
                else
                {
                    _saveError = error ?? "The texture import setting could not be saved.";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_saveError))
            EditorGui.Error(_saveError);

        EditorGui.Space();
        preview.DrawBody(asset);
    }

    private static string DisplaySemantic(TextureSemantic semantic) => semantic switch
    {
        TextureSemantic.Color => "Regular",
        TextureSemantic.NormalMap => "Normal Map",
        _ => semantic.ToString()
    };
}

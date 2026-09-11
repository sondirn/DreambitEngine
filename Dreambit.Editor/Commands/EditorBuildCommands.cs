using Dreambit.Editor.Assets;
using Dreambit.Editor.Compilation;

namespace Dreambit.Editor.Commands;

/// <summary>
/// Read-only presentation data describing the currently loaded game assembly.
/// Keeping this as data prevents editor UI from reaching through the command
/// boundary into GameAssemblyLoadService.
/// </summary>
internal readonly record struct EditorBuildAssemblySummary(
    int Generation,
    int ComponentCount,
    int CustomAssetCount);

/// <summary>
/// Shared semantic operations and read-only status used by editor build/bake UI.
/// </summary>
internal sealed class EditorBuildCommands(
    GameCodeService gameCode,
    AssetBakeService assetBaking)
{
    public GameBuildStatus GameBuildStatus => gameCode.Status;

    public bool IsGameBuildRunning => gameCode.IsRunning;

    public EditorBuildAssemblySummary? LoadedAssembly
    {
        get
        {
            var loaded = gameCode.Assemblies.Current;
            if (loaded is null)
                return null;

            return new EditorBuildAssemblySummary(
                loaded.Generation,
                loaded.Types.ComponentTypes.Count,
                loaded.Types.AssetTypes.Count);
        }
    }

    public void BuildGame() =>
        gameCode.RequestBuild(
            rebuild: false,
            immediate: true);

    public void RebuildGame() =>
        gameCode.RequestBuild(
            rebuild: true,
            immediate: true);

    public void UpdateBlobs() =>
        assetBaking.RequestBake(rebuildAll: false);

    public void RebuildAllBlobs() =>
        assetBaking.RequestBake(rebuildAll: true);

    public void BakePak() =>
        assetBaking.RequestPakBake();
}
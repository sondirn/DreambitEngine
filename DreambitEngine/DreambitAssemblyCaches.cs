using System;
using System.Collections.Generic;
using System.Reflection;
using Dreambit.ECS;

namespace Dreambit;

/// <summary>
/// Releases engine-owned reflection caches before an editor unloads a collectible game assembly.
/// Runtime games normally never need to call this API.
/// </summary>
public static class DreambitAssemblyCaches
{
    public static void Release(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        DreambitAssetTypeRegistry.ReleaseAssembly(assembly);
        PropertyConverterRegistry.ReleaseAssembly(assembly);
        BlueprintResolver.ReleaseAssembly(assembly);
        ComponentRepository.ReleaseAssembly(assembly);
        Resources.ReleaseAssembly(assembly);
    }

    public static void Refresh()
    {
        DreambitAssetTypeRegistry.Refresh();
        DreambitJson.RefreshConverters();
        BlueprintResolver.RebuildComponentTypeRegistry();
        Resources.RefreshLoaders();
    }

    /// <summary>
    /// Refreshes reflection caches using explicit non-engine types. Editors use this overload to
    /// avoid rediscovering an assembly that is leaving a collectible context.
    /// </summary>
    public static void Refresh(
        IEnumerable<Type> assetTypes,
        IEnumerable<Type>? assetLoaderTypes = null,
        IEnumerable<Type>? componentTypes = null)
    {
        ArgumentNullException.ThrowIfNull(assetTypes);
        DreambitAssetTypeRegistry.Refresh(assetTypes);
        DreambitJson.RefreshConverters();
        if (componentTypes is null)
            BlueprintResolver.RebuildComponentTypeRegistry();
        else
            BlueprintResolver.RebuildComponentTypeRegistry(componentTypes);
        Resources.RefreshLoaders(assetLoaderTypes);
    }
}

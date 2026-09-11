using System.Reflection;
using System.Runtime.CompilerServices;
using Dreambit.ECS;
using Dreambit.Editor.Compilation;
using Dreambit.EditorApi;

namespace Dreambit.Editor.Inspection;

internal sealed class EditorTypeRegistry : IDisposable
{
    private readonly GameAssemblyLoadService _assemblies;
    private readonly InspectorMetadataCache _metadata;
    private bool _disposed;

    public EditorTypeRegistry(GameAssemblyLoadService assemblies, InspectorMetadataCache metadata)
    {
        _assemblies = assemblies;
        _metadata = metadata;
        _assemblies.Unloading += OnUnloading;
        _assemblies.Reloaded += OnReloaded;
        Rebuild();
    }

    public IReadOnlyList<Type> ComponentTypes { get; private set; } = [];

    public IReadOnlyList<Type> AssetTypes { get; private set; } = [];

    public void Dispose()
    {
        if (_disposed)
            return;
        _assemblies.Unloading -= OnUnloading;
        _assemblies.Reloaded -= OnReloaded;
        ComponentTypes = [];
        AssetTypes = [];
        _metadata.Clear();
        _disposed = true;
    }

    public event Action? Changed;

    private void OnUnloading(LoadedGameAssembly assembly)
    {
        _metadata.ReleaseAssembly(assembly.Assembly);
        ComponentTypes = ComponentTypes.Where(type => type.Assembly != assembly.Assembly).ToArray();
        AssetTypes = AssetTypes.Where(type => type.Assembly != assembly.Assembly).ToArray();
    }

    private void OnReloaded(LoadedGameAssembly _)
    {
        Rebuild();
    }

    private void Rebuild()
    {
        var gameTypes = _assemblies.Current?.Types;
        ComponentTypes = GetLoadableTypes(typeof(Component).Assembly)
            .Where(IsConcreteComponent)
            .Where(IsComponentViewableInEditor)
            .Concat(gameTypes?.ComponentTypes ?? [])
            .Distinct()
            .OrderBy(type => type.Name)
            .ToArray();
        AssetTypes = GetLoadableTypes(typeof(DreambitAsset).Assembly)
            .Where(IsConcreteAsset)
            .Concat(gameTypes?.AssetTypes ?? [])
            .Distinct()
            .OrderBy(type => type.Name)
            .ToArray();
        Changed?.Invoke();
    }

    private static bool IsConcreteComponent(Type type)
    {
        return typeof(Component).IsAssignableFrom(type) && !type.IsAbstract && !type.IsGenericType &&
               type.GetConstructor(Type.EmptyTypes) is not null;
    }

    private static bool IsComponentViewableInEditor(Type type)
    {
        return !Attribute.IsDefined(
            type,
            typeof(HideComponentInEditorAttribute),
            inherit: false);
    }

    private static bool IsConcreteAsset(Type type)
    {
        return typeof(DreambitAsset).IsAssignableFrom(type) && !type.IsAbstract && !type.IsGenericType &&
               type.GetConstructor(Type.EmptyTypes) is not null;
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}

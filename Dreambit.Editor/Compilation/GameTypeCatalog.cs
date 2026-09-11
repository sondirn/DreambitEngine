using System.Reflection;
using Dreambit;
using Dreambit.ECS;
using Dreambit.EditorApi;

namespace Dreambit.Editor.Compilation;

internal sealed record GameTypeCatalog(
    IReadOnlyList<Type> ComponentTypes,
    IReadOnlyList<Type> AssetTypes,
    IReadOnlyList<Type> AssetLoaderTypes,
    IReadOnlyList<Type> PropertyConverterTypes,
    IReadOnlyList<Type> CustomEditorTypes)
{
    public static GameTypeCatalog Empty { get; } = new([], [], [], [], []);

    public static GameTypeCatalog Discover(Assembly assembly)
    {
        var types = GetLoadableTypes(assembly)
            .Where(type => !type.IsAbstract && !type.IsGenericTypeDefinition)
            .ToArray();
        return new GameTypeCatalog(
            types.Where(type => typeof(Component).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray(),
            types.Where(type => typeof(DreambitAsset).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray(),
            types.Where(type => typeof(IAssetLoader).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray(),
            types.Where(type => typeof(IPropertyConverterMarker).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray(),
            types.Where(type => typeof(IDreambitCustomEditor).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray());
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

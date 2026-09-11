using System;

namespace Dreambit.ECS;

/// <summary>
///     Declares that a scene service must be available and initialized before
///     the attributed service becomes ready.
/// </summary>
[AttributeUsage(
    AttributeTargets.Class,
    AllowMultiple = true,
    Inherited = true)]
public sealed class RequiresSceneServiceAttribute : Attribute
{
    public RequiresSceneServiceAttribute(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (!typeof(SceneServiceComponent).IsAssignableFrom(serviceType))
            throw new ArgumentException(
                $"'{serviceType.FullName}' is not a scene service type.",
                nameof(serviceType));

        ServiceType = serviceType;
    }

    public Type ServiceType { get; }
}

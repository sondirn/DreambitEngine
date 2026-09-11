using System;

namespace Dreambit.Networking.Replication;

/// <summary>
/// Marks a <see cref="ECS.Component"/> as eligible for automatic state replication and assigns its
/// stable protocol component ID. The component must also be registered with
/// <see cref="NetworkReplicationRegistry.Register{T}()"/> before the session starts.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NetworkReplicatedAttribute : Attribute
{
    /// <summary>Creates the marker with a stable, nonzero component protocol ID.</summary>
    /// <param name="componentId">The component ID shared by every compatible build.</param>
    public NetworkReplicatedAttribute(ushort componentId)
    {
        if (componentId == 0)
            throw new ArgumentOutOfRangeException(nameof(componentId));
        ComponentId = componentId;
    }

    /// <summary>Gets the stable component protocol ID.</summary>
    public ushort ComponentId { get; }
}

/// <summary>
/// Marks a mutable field or property for automatic component replication and assigns its stable
/// field ID within that component schema.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>Creates the marker with a stable, nonzero field protocol ID.</summary>
    /// <param name="fieldId">The field ID shared by every compatible build.</param>
    public ReplicatedAttribute(ushort fieldId)
    {
        if (fieldId == 0)
            throw new ArgumentOutOfRangeException(nameof(fieldId));
        FieldId = fieldId;
    }

    /// <summary>Gets the stable field protocol ID within its component schema.</summary>
    public ushort FieldId { get; }

    /// <summary>Maximum UTF-8 bytes for string values.</summary>
    public int MaxLength { get; set; } = 256;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Dreambit.ECS;
using Dreambit.Networking.Protocol;
using Dreambit.Networking.Transport;

namespace Dreambit.Networking.Replication;

/// <summary>
/// Registration-time component schema builder. Reflection is used only while registering a
/// Component; runtime snapshot capture uses cached typed member delegates.
/// </summary>
public sealed class NetworkReplicationRegistry
{
    private static readonly MethodInfo CreateMemberMethod = typeof(NetworkReplicationRegistry)
        .GetMethod(nameof(CreateMember), BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly Dictionary<ushort, NetworkComponentDescriptor> _byId = [];
    private readonly Dictionary<Type, NetworkComponentDescriptor> _byType = [];
    private bool _frozen;

    /// <summary>
    /// Gets the deterministic hash of the registered Component schemas. The connection handshake
    /// rejects peers whose replication schemas differ.
    /// </summary>
    public NetworkSchemaHash SchemaHash => BuildSchemaHash();

    /// <summary>
    /// Registers a Component for automatic replication using its
    /// <see cref="NetworkReplicatedAttribute"/> and <see cref="ReplicatedAttribute"/> members.
    /// Reflection is used during this registration only; runtime capture uses cached delegates.
    /// </summary>
    /// <typeparam name="T">The Component type to replicate.</typeparam>
    /// <remarks>
    /// Supported automatic values include numeric primitives, Boolean, GUID, string,
    /// <see cref="AssetId"/>, <see cref="NetworkEntityRef"/>, MonoGame vectors, quaternion, color,
    /// and enums. Use <see cref="NetworkEntityRef"/> instead of raw Entity or Component references,
    /// and replicate an asset's <see cref="AssetId"/> instead of the asset object.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A session is active, metadata is missing or invalid, the type is already registered, or a
    /// registered replicated Component appears on a network root's descendant.
    /// </exception>
    public void Register<T>() where T : Component
    {
        EnsureMutable();
        var componentType = typeof(T);
        var attribute = componentType.GetCustomAttribute<NetworkReplicatedAttribute>() ??
                        throw new InvalidOperationException(
                            $"Replicated Component '{componentType.FullName}' must declare NetworkReplicatedAttribute.");
        RegisterDescriptor(CreateAutomaticDescriptor<T>(attribute.ComponentId));
    }

    /// <summary>Registers a Component using an explicit bounded codec.</summary>
    /// <typeparam name="T">The Component type to replicate.</typeparam>
    /// <param name="componentId">A stable nonzero protocol ID unique to this Component type.</param>
    /// <param name="maximumPayload">
    /// The maximum encoded state size in bytes, from 1 through
    /// <see cref="NetworkOptions.DefaultMaxProtocolPayload"/>.
    /// </param>
    /// <param name="codec">The serializer that captures and applies the Component's state.</param>
    /// <exception cref="InvalidOperationException">
    /// A session is active, or the component ID or type has already been registered.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="componentId"/> is zero or <paramref name="maximumPayload"/> is out of range.
    /// </exception>
    public void Register<T>(
        ushort componentId,
        int maximumPayload,
        INetworkComponentCodec<T> codec) where T : Component
    {
        EnsureMutable();
        if (componentId == 0)
            throw new ArgumentOutOfRangeException(nameof(componentId));
        if (maximumPayload is < 1 or > NetworkOptions.DefaultMaxProtocolPayload)
            throw new ArgumentOutOfRangeException(nameof(maximumPayload));
        ArgumentNullException.ThrowIfNull(codec);
        RegisterDescriptor(new CustomNetworkComponentDescriptor<T>(
            componentId,
            maximumPayload,
            codec));
    }

    internal void Freeze() => _frozen = true;
    internal void Unfreeze() => _frozen = false;

    internal IReadOnlyList<NetworkReplicationBinding> CreateBindings(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_byType.Count == 0)
            return [];

        ValidateEntityShape(entity);

        var bindings = new List<NetworkReplicationBinding>();
        // This scan happens once when NetworkWorld registers the Entity, not each snapshot.
        foreach (var component in entity.GetAllComponents())
            if (_byType.TryGetValue(component.GetType(), out var descriptor))
                bindings.Add(new NetworkReplicationBinding
                {
                    Component = component,
                    Descriptor = descriptor
                });
        bindings.Sort((left, right) => left.Descriptor.Id.CompareTo(right.Descriptor.Id));
        return bindings;
    }

    internal void ValidateForTransport(
        TransportCapabilities capabilities,
        int maximumProtocolPayload)
    {
        const int snapshotPayloadOverhead = 4 + 8 + 2 + 4;
        const int baselinePayloadOverhead = 1 + 8 + 2 + 4;
        foreach (var descriptor in _byId.Values)
        {
            var snapshotPacketSize = checked(
                NetworkProtocol.HeaderLength + snapshotPayloadOverhead + descriptor.MaximumPayload);
            if (snapshotPayloadOverhead + descriptor.MaximumPayload > maximumProtocolPayload)
                throw new InvalidOperationException(
                    $"Replicated Component '{descriptor.ComponentType.FullName}' ({descriptor.Id}) can require " +
                    $"{descriptor.MaximumPayload} bytes and cannot fit the configured protocol payload limit " +
                    $"of {maximumProtocolPayload} bytes.");
            if (snapshotPacketSize > capabilities.MaxUnreliablePayload)
                throw new InvalidOperationException(
                    $"Replicated Component '{descriptor.ComponentType.FullName}' ({descriptor.Id}) can require " +
                    $"a {snapshotPacketSize}-byte snapshot packet, exceeding the active transport's " +
                    $"{capabilities.MaxUnreliablePayload}-byte unreliable payload limit.");

            var baselinePacketSize = checked(
                NetworkProtocol.HeaderLength + baselinePayloadOverhead + descriptor.MaximumPayload);
            if (baselinePacketSize > capabilities.MaxReliablePayload)
                throw new InvalidOperationException(
                    $"Replicated Component '{descriptor.ComponentType.FullName}' ({descriptor.Id}) can require " +
                    $"a {baselinePacketSize}-byte baseline packet, exceeding the active transport's " +
                    $"{capabilities.MaxReliablePayload}-byte reliable payload limit.");
        }
    }

    internal void ValidateEntityShape(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (_byType.Count != 0)
            ValidateNoReplicatedDescendants(entity, entity);
    }

    internal NetworkComponentDescriptor GetById(ushort id) =>
        _byId.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new NetworkProtocolException($"Replicated Component ID {id} is not registered.");

    internal void EnsureMutable()
    {
        if (_frozen)
            throw new InvalidOperationException("Replication registrations are frozen while a session is active.");
    }

    private void ValidateNoReplicatedDescendants(Entity root, Entity parent)
    {
        foreach (var child in parent.Children)
        {
            foreach (var component in child.GetAllComponents())
                if (_byType.ContainsKey(component.GetType()))
                    throw new InvalidOperationException(
                        $"Network root '{root.Name}' contains registered replicated Component " +
                        $"'{component.GetType().FullName}' on child Entity '{child.Name}'. " +
                        "Version 1 replication supports Components on the network root only.");
            ValidateNoReplicatedDescendants(root, child);
        }
    }

    private void RegisterDescriptor(NetworkComponentDescriptor descriptor)
    {
        if (_byId.ContainsKey(descriptor.Id))
            throw new InvalidOperationException(
                $"Replicated Component ID {descriptor.Id} is already registered.");
        if (_byType.ContainsKey(descriptor.ComponentType))
            throw new InvalidOperationException(
                $"Replicated Component type '{descriptor.ComponentType.FullName}' is already registered.");
        _byId.Add(descriptor.Id, descriptor);
        _byType.Add(descriptor.ComponentType, descriptor);
    }

    private static NetworkComponentDescriptor CreateAutomaticDescriptor<T>(ushort componentId)
        where T : Component
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var candidates = new List<(MemberInfo Member, ReplicatedAttribute Attribute)>();
        foreach (var field in typeof(T).GetFields(flags))
            if (field.GetCustomAttribute<ReplicatedAttribute>() is { } attribute)
                candidates.Add((field, attribute));
        foreach (var property in typeof(T).GetProperties(flags))
            if (property.GetCustomAttribute<ReplicatedAttribute>() is { } attribute)
                candidates.Add((property, attribute));

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"Replicated Component '{typeof(T).FullName}' has no Replicated fields or properties.");

        var members = new List<INetworkReplicatedMember>(candidates.Count);
        var ids = new HashSet<ushort>();
        foreach (var candidate in candidates.OrderBy(value => value.Attribute.FieldId))
        {
            if (!ids.Add(candidate.Attribute.FieldId))
                throw new InvalidOperationException(
                    $"Replicated Component '{typeof(T).FullName}' uses field ID " +
                    $"{candidate.Attribute.FieldId} more than once.");

            var valueType = candidate.Member switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo property => property.PropertyType,
                _ => throw new InvalidOperationException("Unsupported replicated member metadata.")
            };
            if (typeof(Entity).IsAssignableFrom(valueType) ||
                typeof(Component).IsAssignableFrom(valueType))
                throw new InvalidOperationException(
                    $"Replicated member '{typeof(T).FullName}.{candidate.Member.Name}' cannot use raw " +
                    $"{valueType.Name} references. Use NetworkEntityRef instead.");
            if (typeof(DreambitAsset).IsAssignableFrom(valueType))
                throw new InvalidOperationException(
                    $"Replicated member '{typeof(T).FullName}.{candidate.Member.Name}' cannot use an asset " +
                    "object reference. Replicate its AssetId instead.");

            try
            {
                var method = CreateMemberMethod.MakeGenericMethod(typeof(T), valueType);
                members.Add((INetworkReplicatedMember)method.Invoke(
                    null,
                    [candidate.Member, candidate.Attribute])!);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        return new AutomaticNetworkComponentDescriptor<T>(componentId, members);
    }

    private static INetworkReplicatedMember CreateMember<TComponent, TValue>(
        MemberInfo member,
        ReplicatedAttribute attribute) where TComponent : Component
    {
        var component = Expression.Parameter(typeof(TComponent), "component");
        Expression access;
        switch (member)
        {
            case FieldInfo { IsStatic: false, IsInitOnly: false } field:
                access = Expression.Field(component, field);
                break;
            case FieldInfo field:
                throw new InvalidOperationException(
                    $"Replicated field '{field.DeclaringType?.FullName}.{field.Name}' must be mutable and non-static.");
            case PropertyInfo property when
                property.GetMethod is { IsStatic: false } &&
                property.SetMethod is { IsStatic: false } &&
                property.GetIndexParameters().Length == 0:
                access = Expression.Property(component, property);
                break;
            case PropertyInfo property:
                throw new InvalidOperationException(
                    $"Replicated property '{property.DeclaringType?.FullName}.{property.Name}' must have " +
                    "instance get and set accessors and cannot be an indexer.");
            default:
                throw new InvalidOperationException("Only fields and properties can be replicated.");
        }

        var getter = Expression.Lambda<Func<TComponent, TValue>>(access, component).Compile();
        var value = Expression.Parameter(typeof(TValue), "value");
        var setter = Expression.Lambda<Action<TComponent, TValue>>(
            Expression.Assign(access, value),
            component,
            value).Compile();
        var codec = NetworkValueCodecs.Resolve<TValue>(attribute.MaxLength, member);
        return new NetworkReplicatedMember<TComponent, TValue>(
            attribute.FieldId,
            member.Name,
            getter,
            setter,
            codec);
    }

    private NetworkSchemaHash BuildSchemaHash()
    {
        var schema = new StringBuilder();
        foreach (var descriptor in _byId.Values.OrderBy(value => value.Id))
            schema.Append(descriptor.SchemaToken).Append('\n');
        return NetworkSchemaHash.Compute(schema.ToString());
    }
}

internal abstract class NetworkComponentDescriptor
{
    public abstract ushort Id { get; }
    public abstract Type ComponentType { get; }
    public abstract int MaximumPayload { get; }
    public abstract string SchemaToken { get; }
    public abstract void Write(NetworkWriter writer, Component component);
    public abstract void Read(ref NetworkReader reader, Component component);
}

internal sealed class AutomaticNetworkComponentDescriptor<T> : NetworkComponentDescriptor
    where T : Component
{
    private readonly IReadOnlyList<INetworkReplicatedMember> _members;

    public AutomaticNetworkComponentDescriptor(
        ushort id,
        IReadOnlyList<INetworkReplicatedMember> members)
    {
        Id = id;
        _members = members;
        MaximumPayload = members.Sum(member => member.MaximumSize);
        if (MaximumPayload > NetworkOptions.DefaultMaxProtocolPayload)
            throw new InvalidOperationException(
                $"Replicated Component '{typeof(T).FullName}' has a maximum payload of " +
                $"{MaximumPayload} bytes, exceeding {NetworkOptions.DefaultMaxProtocolPayload}. " +
                "Use a smaller schema or a bounded custom Component codec.");
        SchemaToken = $"{Id}|{typeof(T).FullName}|" +
                      string.Join(";", members.Select(member => member.SchemaToken));
    }

    public override ushort Id { get; }
    public override Type ComponentType => typeof(T);
    public override int MaximumPayload { get; }
    public override string SchemaToken { get; }

    public override void Write(NetworkWriter writer, Component component)
    {
        if (component is not T typed)
            throw new InvalidOperationException($"Expected Component '{typeof(T).FullName}'.");
        foreach (var member in _members)
            member.Write(writer, typed);
    }

    public override void Read(ref NetworkReader reader, Component component)
    {
        if (component is not T typed)
            throw new NetworkProtocolException($"Expected Component '{typeof(T).FullName}'.");
        foreach (var member in _members)
            member.Read(ref reader, typed);
    }
}

internal sealed class CustomNetworkComponentDescriptor<T> : NetworkComponentDescriptor
    where T : Component
{
    private readonly INetworkComponentCodec<T> _codec;

    public CustomNetworkComponentDescriptor(
        ushort id,
        int maximumPayload,
        INetworkComponentCodec<T> codec)
    {
        Id = id;
        MaximumPayload = maximumPayload;
        _codec = codec;
        SchemaToken = $"{Id}|{typeof(T).FullName}|custom|{maximumPayload}|{codec.GetType().FullName}";
    }

    public override ushort Id { get; }
    public override Type ComponentType => typeof(T);
    public override int MaximumPayload { get; }
    public override string SchemaToken { get; }

    public override void Write(NetworkWriter writer, Component component)
    {
        if (component is not T typed)
            throw new InvalidOperationException($"Expected Component '{typeof(T).FullName}'.");
        _codec.Write(writer, typed);
    }

    public override void Read(ref NetworkReader reader, Component component)
    {
        if (component is not T typed)
            throw new NetworkProtocolException($"Expected Component '{typeof(T).FullName}'.");
        _codec.Read(ref reader, typed);
    }
}

internal interface INetworkReplicatedMember
{
    ushort Id { get; }
    int MaximumSize { get; }
    string SchemaToken { get; }
    void Write(NetworkWriter writer, Component component);
    void Read(ref NetworkReader reader, Component component);
}

internal sealed class NetworkReplicatedMember<TComponent, TValue> : INetworkReplicatedMember
    where TComponent : Component
{
    private readonly Func<TComponent, TValue> _getter;
    private readonly Action<TComponent, TValue> _setter;
    private readonly INetworkValueCodec<TValue> _codec;

    public NetworkReplicatedMember(
        ushort id,
        string name,
        Func<TComponent, TValue> getter,
        Action<TComponent, TValue> setter,
        INetworkValueCodec<TValue> codec)
    {
        Id = id;
        _getter = getter;
        _setter = setter;
        _codec = codec;
        SchemaToken = $"{id}:{codec.SchemaToken}";
    }

    public ushort Id { get; }
    public int MaximumSize => _codec.MaximumSize;
    public string SchemaToken { get; }

    public void Write(NetworkWriter writer, Component component) =>
        _codec.Write(writer, _getter((TComponent)component));

    public void Read(ref NetworkReader reader, Component component) =>
        _setter((TComponent)component, _codec.Read(ref reader));
}

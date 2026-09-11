using System;
using Dreambit.ECS;
using Dreambit.Networking.Protocol;

namespace Dreambit.Networking.Replication;

internal sealed class NetworkReplicationBinding
{
    public required Component Component { get; init; }
    public required NetworkComponentDescriptor Descriptor { get; init; }

    public byte[] Capture()
    {
        EnsureAlive();
        using var writer = new NetworkWriter(
            System.Math.Min(Descriptor.MaximumPayload, 256),
            Descriptor.MaximumPayload);
        Descriptor.Write(writer, Component);
        return writer.ToArray();
    }

    public void Apply(ReadOnlySpan<byte> payload)
    {
        EnsureAlive();
        if (payload.Length > Descriptor.MaximumPayload)
            throw new NetworkProtocolException(
                $"Replicated component {Descriptor.Id} payload length {payload.Length} " +
                $"exceeds {Descriptor.MaximumPayload}.");
        var reader = new NetworkReader(payload);
        Descriptor.Read(ref reader, Component);
        reader.EnsureComplete();
    }

    private void EnsureAlive()
    {
        if (Component.IsDestroyed || Component.Entity is null || Entity.IsDestroyed(Component.Entity))
            throw new InvalidOperationException(
                $"Replicated Component {Descriptor.Id} was removed while its network Entity remains " +
                "registered. Replicated Component topology is fixed for the Entity lifetime; despawn " +
                "and respawn the Entity to change it.");
    }
}

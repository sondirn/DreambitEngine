using System;
using Dreambit.ECS;
using Dreambit.Networking.Messaging;

namespace Dreambit.Networking;

/// <summary>
/// Provides the high-level registration API exposed to game networking modules.
/// </summary>
public sealed class NetworkRegistrationContext
{
    private readonly NetworkService _network;

    internal NetworkRegistrationContext(
        NetworkService network)
    {
        ArgumentNullException.ThrowIfNull(network);

        _network = network;
    }

    /// <summary>
    /// Registers a self-describing gameplay message with its receive handler.
    /// </summary>
    public void Handle<T>(
        Action<NetworkMessageContext, T> handler)
        where T : INetworkMessage<T>
    {
        ArgumentNullException.ThrowIfNull(handler);

        _network.Messages.Register(handler);
    }

    /// <summary>
    /// Registers a Component using its NetworkReplicatedAttribute schema.
    /// </summary>
    public void Replicate<T>()
        where T : Component
    {
        _network.Replication.Register<T>();
    }
}
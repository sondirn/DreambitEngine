using Dreambit.ECS;
using Dreambit.Networking.Protocol;

namespace Dreambit.Networking.Replication;

/// <summary>
/// Explicit serializer for a replicated Component whose state is not described with
/// <see cref="ReplicatedAttribute"/> members.
/// </summary>
/// <typeparam name="T">The replicated Component type.</typeparam>
public interface INetworkComponentCodec<T> where T : Component
{
    /// <summary>Writes the complete authoritative state for one Component instance.</summary>
    /// <param name="writer">The bounded writer receiving the state.</param>
    /// <param name="component">The authoritative Component instance.</param>
    void Write(NetworkWriter writer, T component);

    /// <summary>Reads and applies complete authoritative state to one Component instance.</summary>
    /// <param name="reader">The reader positioned at the first byte of Component state.</param>
    /// <param name="component">The local Component instance to update.</param>
    void Read(ref NetworkReader reader, T component);
}

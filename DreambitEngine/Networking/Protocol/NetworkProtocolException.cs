using System;

namespace Dreambit.Networking.Protocol;

/// <summary>
/// Represents malformed, out-of-order, incompatible, or otherwise invalid network protocol data.
/// Receiving one normally causes Dreambit to reject or disconnect the offending connection.
/// </summary>
public sealed class NetworkProtocolException : Exception
{
    /// <summary>Creates a protocol exception with a diagnostic message.</summary>
    /// <param name="message">A description of the protocol violation.</param>
    public NetworkProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a protocol exception with a diagnostic message and underlying cause.</summary>
    /// <param name="message">A description of the protocol violation.</param>
    /// <param name="innerException">The exception that caused protocol processing to fail.</param>
    public NetworkProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

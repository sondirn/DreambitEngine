using System;
using System.Net;

namespace Dreambit.Networking.Direct;

/// <summary>
/// Configures limits and timeouts for a <see cref="DirectIpTransport"/>. Values are validated
/// when a transport instance is created.
/// </summary>
public sealed class DirectIpOptions
{
    /// <summary>
    /// Gets or sets how long a client waits for its TCP connection to complete. Valid values are
    /// greater than zero and no more than five minutes.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the maximum TCP-carried reliable payload in bytes. Valid values are 256 bytes
    /// through 16 MiB.
    /// </summary>
    public int MaxReliablePayload { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum UDP-carried unreliable payload in bytes. Valid values are 64
    /// through 1,400 bytes; the default leaves room for internet protocol overhead.
    /// </summary>
    public int MaxUnreliablePayload { get; set; } = 1150;

    /// <summary>
    /// Gets or sets the maximum number of connection and receive events queued by transport worker
    /// threads. Valid values are 16 through 65,536.
    /// </summary>
    public int MaxQueuedEvents { get; set; } = 2048;

    /// <summary>
    /// Gets or sets the number of logical transport channels. Values must be nonzero;
    /// Dreambit's current network session requires at least four channels.
    /// </summary>
    public byte MaxChannels { get; set; } = 4;

    internal void Validate()
    {
        if (ConnectionTimeout <= TimeSpan.Zero || ConnectionTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeout));
        if (MaxReliablePayload is < 256 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxReliablePayload));
        if (MaxUnreliablePayload is < 64 or > 1400)
            throw new ArgumentOutOfRangeException(nameof(MaxUnreliablePayload));
        if (MaxQueuedEvents is < 16 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(MaxQueuedEvents));
        if (MaxChannels == 0)
            throw new ArgumentOutOfRangeException(nameof(MaxChannels));
    }
}

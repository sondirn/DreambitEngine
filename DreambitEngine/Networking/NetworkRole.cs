namespace Dreambit.Networking;

/// <summary>Describes the local process's role in the active network session.</summary>
public enum NetworkRole : byte
{
    /// <summary>No network session is active.</summary>
    Offline = 0,

    /// <summary>The process is an authoritative dedicated server without a local client peer.</summary>
    Server = 1,

    /// <summary>The process is an authoritative server and also has a local client peer.</summary>
    Host = 2,

    /// <summary>The process is a remote client connected to an authoritative server.</summary>
    Client = 3
}

using System;
using System.Security.Cryptography;
using System.Text;

namespace Dreambit.Networking.Protocol;

/// <summary>
/// A hexadecimal SHA-256 hash of a canonical network schema, used during the handshake to detect
/// incompatible message or component registrations.
/// </summary>
/// <param name="Hex">The uppercase hexadecimal SHA-256 value.</param>
public readonly record struct NetworkSchemaHash(string Hex)
{
    /// <summary>Computes a SHA-256 schema hash from UTF-8 canonical schema text.</summary>
    /// <param name="canonicalSchema">The stable canonical representation of a schema.</param>
    /// <returns>The computed hexadecimal hash.</returns>
    public static NetworkSchemaHash Compute(string canonicalSchema)
    {
        ArgumentNullException.ThrowIfNull(canonicalSchema);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSchema));
        return new NetworkSchemaHash(Convert.ToHexString(bytes));
    }

    /// <summary>Gets the hash of an empty schema.</summary>
    public static NetworkSchemaHash Empty { get; } = Compute(string.Empty);

    /// <summary>Returns the hexadecimal schema hash.</summary>
    public override string ToString() => Hex;
}

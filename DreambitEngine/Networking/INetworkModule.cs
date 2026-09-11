namespace Dreambit.Networking;

/// <summary>
/// Defines one explicit group of game networking registrations.
///
/// Modules should normally be organized around gameplay features such as
/// player movement, inventory, farming, combat, or world interaction.
/// </summary>
public interface INetworkModule
{
    /// <summary>
    /// Registers this module's networking contract.
    /// </summary>
    void Register(NetworkRegistrationContext network);
}
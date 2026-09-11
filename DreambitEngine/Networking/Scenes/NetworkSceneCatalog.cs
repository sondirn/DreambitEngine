using System;
using System.Collections.Generic;
using System.Text;

namespace Dreambit.Networking.Scenes;

/// <summary>Persistent game-defined mapping from stable network Scene keys to local factories.</summary>
public sealed class NetworkSceneCatalog
{
    private readonly Dictionary<string, Func<Scene>> _factories =
        new(StringComparer.Ordinal);
    private bool _frozen;

    /// <summary>Registers a stable synchronized-scene key and its local Scene factory.</summary>
    /// <param name="key">
    /// The case-sensitive key shared by server and clients. Its UTF-8 representation must be no
    /// more than 256 bytes.
    /// </param>
    /// <param name="factory">
    /// A factory that creates a new local Scene whenever this key is entered. Use
    /// <see cref="RegisterBlueprint{TScene}(string,string)"/> for an editor-authored Scene.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A session is active or <paramref name="key"/> is already registered.
    /// </exception>
    public void Register(string key, Func<Scene> factory)
    {
        if (_frozen)
            throw new InvalidOperationException(
                "Network Scene registrations are frozen while a session is active.");
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        if (Encoding.UTF8.GetByteCount(key) > 256)
            throw new ArgumentException("A network Scene key cannot exceed 256 UTF-8 bytes.", nameof(key));
        if (!_factories.TryAdd(key, factory))
            throw new InvalidOperationException($"Network Scene key '{key}' is already registered.");
    }

    /// <summary>
    /// Registers an editor-authored Scene Blueprint using an ordinary runtime Scene host.
    /// The asset is loaded eagerly whenever the synchronized Scene is entered.
    /// </summary>
    /// <remarks>
    /// Tiled-linked Scene Blueprints require the generic overload with a TiledScene-derived host.
    /// </remarks>
    public void RegisterBlueprint(string key, string sceneAssetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        Register(key, () => Scene.CreateFromBlueprint(sceneAssetName));
    }

    /// <summary>
    /// Registers an editor-authored Scene Blueprint using the requested runtime Scene type.
    /// The asset is eagerly materialized before the Scene is assigned to a NetworkWorld.
    /// </summary>
    /// <typeparam name="TScene">
    /// Runtime behavior and source-integration host for the Scene. Use a TiledScene-derived type
    /// when the Scene Blueprint is linked to a Tiled map.
    /// </typeparam>
    public void RegisterBlueprint<TScene>(string key, string sceneAssetName)
        where TScene : Scene, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneAssetName);
        Register(key, () => Scene.CreateFromBlueprint<TScene>(sceneAssetName));
    }

    /// <summary>Determines whether a non-empty Scene key is registered.</summary>
    /// <param name="key">The case-sensitive key to find.</param>
    /// <returns><see langword="true"/> when the key has a registered factory.</returns>
    public bool Contains(string key) =>
        !string.IsNullOrWhiteSpace(key) && _factories.ContainsKey(key);

    internal Scene Create(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_factories.TryGetValue(key, out var factory))
            throw new KeyNotFoundException($"Network Scene key '{key}' is not registered.");
        return factory() ??
               throw new InvalidOperationException($"Network Scene factory '{key}' returned null.");
    }

    internal void Freeze() => _frozen = true;
    internal void Unfreeze() => _frozen = false;
}

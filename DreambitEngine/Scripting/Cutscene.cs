using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Dreambit.Scripting;

/// <summary>
///     Reusable cutscene data loaded through <see cref="Resources"/>.
/// </summary>
[DreambitAssetType("dreambit.cutscene", FileExtension = DreambitAssetFileExtensions.Cutscene)]
public sealed class Cutscene : DreambitAsset
{
    private readonly ReadOnlyCollection<CutsceneGroup> _groups;

    public Cutscene(IEnumerable<CutsceneGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var materialized = new List<CutsceneGroup>(groups);
        if (materialized.Exists(group => group is null)) throw new ArgumentException("Cutscene groups cannot be null.", nameof(groups));
        _groups = materialized.AsReadOnly();

        if (_groups.Count == 0)
            throw new ArgumentException("A cutscene must contain at least one group.", nameof(groups));
    }

    /// <summary>
    ///     Ordered groups of actions. Actions in a group run in parallel.
    /// </summary>
    public IReadOnlyList<CutsceneGroup> Groups => _groups;
}

public sealed class CutsceneGroup
{
    private readonly ReadOnlyCollection<CutsceneAction> _actions;

    public CutsceneGroup(IEnumerable<CutsceneAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var materialized = new List<CutsceneAction>(actions);
        if (materialized.Exists(action => action is null)) throw new ArgumentException("Cutscene actions cannot be null.", nameof(actions));
        _actions = materialized.AsReadOnly();

        if (_actions.Count == 0)
            throw new ArgumentException("A cutscene group must contain at least one action.", nameof(actions));
    }

    public IReadOnlyList<CutsceneAction> Actions => _actions;
}

public sealed class CutsceneAction
{
    private readonly ReadOnlyDictionary<string, object> _arguments;

    public CutsceneAction(string script, IReadOnlyDictionary<string, object> arguments = null)
    {
        if (string.IsNullOrWhiteSpace(script))
            throw new ArgumentException("A cutscene action must name a script type.", nameof(script));

        Script = script.Trim();
        _arguments = new ReadOnlyDictionary<string, object>(arguments is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(arguments, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Simple or fully-qualified name of a <see cref="ScriptAction"/> type.
    /// </summary>
    public string Script { get; }

    /// <summary>
    ///     Constructor arguments for the script action.
    /// </summary>
    public IReadOnlyDictionary<string, object> Arguments => _arguments;
}

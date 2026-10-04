using System;
using System.Collections.Generic;
using System.IO;

namespace Dreambit.Scripting;

/// <summary>One nonblocking presentation at a time, owned exclusively by its Scene.</summary>
public sealed class ScriptingManager
{
    private readonly Scene _scene;
    private readonly Logger<ScriptingManager> _logger = new();
    private ScriptAction[][] _groups;
    private int _group;
    private bool _executing, _finishing, _disposed;
    private string _cancelReason;
    public Action OnScriptingStart;
    public Action OnScriptingEnd;
    public ScriptingManager(Scene scene) => _scene = scene ?? throw new ArgumentNullException(nameof(scene));
    public static ScriptingManager Instance => Scene.Instance.ScriptingManager;
    public CutscenePlayback Current { get; private set; }
    public bool IsCutsceneActive => Current is not null;

    public bool StartCutscene(string assetName)
    {
        if (IsCutsceneActive || _disposed || _finishing) return false;
        var asset = Resources.LoadAsset<Cutscene>(assetName);
        return asset is not null && StartCutscene(asset);
    }
    public bool StartCutscene(Cutscene cutscene) => TryStart(cutscene, new CutsceneContext(_scene), out _, out _);

    public bool TryStart(Cutscene cutscene, CutsceneContext context, out CutscenePlayback playback, out string error)
    {
        ArgumentNullException.ThrowIfNull(cutscene);
        ArgumentNullException.ThrowIfNull(context);
        playback = null;
        error = null;
        if (_disposed || _finishing || Current is not null)
        { error = "The Scene runner is stopped or already owns a playback."; return false; }
        try
        {
            if (!ReferenceEquals(context.Scene, _scene) || !context.IsValid)
                throw new InvalidOperationException("Playback requires a live context belonging to this Scene.");
            _groups = CreateActions(cutscene, context);
        }
        catch (Exception exception) { error = exception.ToString(); return false; }
        _group = 0;
        _cancelReason = null;
        Current = playback = new CutscenePlayback(cutscene, context, Cancel);
        _executing = true;
        try { OnScriptingStart?.Invoke(); }
        catch (Exception exception) { error = exception.ToString(); }
        finally { _executing = false; }
        if (error is not null) { Finish(CutscenePlaybackStatus.Failed, error); return false; }
        if (_cancelReason is not null) Finish(CutscenePlaybackStatus.Cancelled, _cancelReason);
        return true;
    }

    /// <summary>Validates all constructor arguments without starting actions. Constructors must be side-effect free.</summary>
    public static void Validate(Cutscene cutscene)
    {
        var errors = Release(CreateActions(cutscene, null));
        if (errors.Count > 0) throw new AggregateException("Cutscene validation cleanup failed.", errors);
    }
    private static ScriptAction[][] CreateActions(Cutscene cutscene, CutsceneContext context)
    {
        var groups = new ScriptAction[cutscene.Groups.Count][];
        try
        {
            for (var g = 0; g < groups.Length; g++)
            {
                var definitions = cutscene.Groups[g].Actions;
                groups[g] = new ScriptAction[definitions.Count];
                for (var a = 0; a < definitions.Count; a++)
                    try
                    {
                        groups[g][a] = ScriptFactory.CreateScript(definitions[a]);
                        groups[g][a].Context = context;
                    }
                    catch (Exception exception)
                    {
                        throw new InvalidDataException($"Cutscene '{cutscene.AssetName}' ({cutscene.AssetId}), group {g + 1}, action {a + 1} '{definitions[a].Script}': {exception.Message}", exception);
                    }
            }
            return groups;
        }
        catch (Exception exception)
        {
            var errors = Release(groups);
            if (errors.Count > 0) { errors.Insert(0, exception); throw new AggregateException(errors); }
            throw;
        }
    }
    public void Update()
    {
        if (Current is null || _executing || _finishing) return;
        _executing = true;
        var completed = false;
        string failure = null;
        var actionIndex = 0;
        try
        {
            if (!Current.Context.IsValid) _cancelReason = "A required actor or content instance is no longer available.";
            else
            {
                var group = _groups[_group];
                completed = true;
                for (; actionIndex < group.Length; actionIndex++)
                {
                    var action = group[actionIndex];
                    if (!action.Finished) action.Update();
                    if (_cancelReason is not null) break;
                    if (!action.Finished) completed = false;
                }
                if (completed && _cancelReason is null)
                {
                    for (actionIndex = 0; actionIndex < group.Length; actionIndex++)
                    { group[actionIndex].OnGroupEnd(); if (_cancelReason is not null) break; }
                    _group++;
                }
            }
        }
        catch (Exception exception)
        { failure = $"Cutscene '{Current.Asset.AssetName}' ({Current.Asset.AssetId}), group {_group + 1}, action {actionIndex + 1}: {exception}"; }
        finally { _executing = false; }
        if (failure is not null) Finish(CutscenePlaybackStatus.Failed, failure);
        else if (_cancelReason is not null) Finish(CutscenePlaybackStatus.Cancelled, _cancelReason);
        else if (completed && _group == _groups.Length) Finish(CutscenePlaybackStatus.Completed, null);
    }
    public void Cancel(string reason = "Cancelled by caller.")
    {
        if (Current is null || _finishing) return;
        _cancelReason = reason ?? "Cancelled by caller.";
        if (!_executing) Finish(CutscenePlaybackStatus.Cancelled, _cancelReason);
    }
    private void Finish(CutscenePlaybackStatus status, string diagnostic)
    {
        _finishing = true;
        var playback = Current;
        var errors = Release(_groups);
        if (errors.Count > 0)
        { status = CutscenePlaybackStatus.Failed; diagnostic = (diagnostic ?? string.Empty) + new AggregateException("Action cleanup failed.", errors); }
        _groups = null;
        Current = null;
        _cancelReason = null;
        _finishing = false;
        if (status == CutscenePlaybackStatus.Failed) _logger.Error(diagnostic);
        playback.Finish(status, diagnostic);
        // Compatibility notification is success-only. New callers should observe their playback handle.
        if (status == CutscenePlaybackStatus.Completed && OnScriptingEnd is { } handlers)
            foreach (Action handler in handlers.GetInvocationList())
                try { handler(); } catch (Exception exception) { _logger.Error(exception.ToString()); }
    }
    private static List<Exception> Release(ScriptAction[][] groups)
    {
        var errors = new List<Exception>();
        if (groups is null) return errors;
        for (var g = groups.Length - 1; g >= 0; g--)
            if (groups[g] is { } actions)
                for (var a = actions.Length - 1; a >= 0; a--)
                    if (actions[a] is { } action)
                        try { action.CleanUp(); } catch (Exception exception) { errors.Add(exception); }
        return errors;
    }
    internal void CleanUp()
    {
        _disposed = true;
        Cancel("Scene shutdown.");
        OnScriptingStart = null;
        OnScriptingEnd = null;
    }
}

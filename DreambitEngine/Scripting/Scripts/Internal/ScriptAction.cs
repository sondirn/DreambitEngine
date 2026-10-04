namespace Dreambit.Scripting;

public abstract class ScriptAction
{
    public CutsceneContext Context { get; internal set; }
    /// <summary>Release temporary state even if never started. Called once per instance.</summary>
    public virtual void CleanUp() { }
    internal bool IsStarted;
    private bool _completionNotified;
    internal bool Finished => IsComplete && _completionNotified;
    public bool IsComplete { get; set; } = false;

    /// <summary>
    ///     Called every frame
    /// </summary>
    public abstract void OnUpdate();

    /// <summary>
    ///     Called once when this script has started, before the first update
    /// </summary>
    public virtual void OnStart()
    {
    }

    /// <summary>
    ///     Called once when the script is completed, before the script group has ended
    /// </summary>
    public virtual void OnCompleted()
    {
    }

    /// <summary>
    ///     Called once after all scripts in the current group have ended
    /// </summary>
    public virtual void OnGroupEnd()
    {
    }

    internal void Update()
    {
        if (!IsStarted)
        {
            IsStarted = true;
            OnStart();
        }

        if (!IsComplete) OnUpdate();

        if (IsComplete && !_completionNotified)
        {
            _completionNotified = true;
            OnCompleted();
        }
    }
}

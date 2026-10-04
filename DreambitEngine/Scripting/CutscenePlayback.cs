using System;

namespace Dreambit.Scripting;

public enum CutscenePlaybackStatus { Running, Completed, Cancelled, Failed }

public sealed class CutscenePlayback
{
    private Action<string> _cancel;
    internal CutscenePlayback(Cutscene asset, CutsceneContext context, Action<string> cancel)
    { Asset = asset; Context = context; _cancel = cancel; }
    public Guid Id { get; } = Guid.NewGuid();
    public Cutscene Asset { get; }
    public CutsceneContext Context { get; }
    public CutscenePlaybackStatus Status { get; private set; }
    public string Diagnostic { get; private set; }
    public event Action<CutscenePlayback> Finished;
    public void Cancel(string reason = "Cancelled by owner.") => _cancel?.Invoke(reason);
    internal void Finish(CutscenePlaybackStatus status, string diagnostic)
    {
        Status = status;
        Diagnostic = diagnostic;
        _cancel = null;
        var handlers = Finished;
        Finished = null;
        if (handlers is null) return;
        foreach (Action<CutscenePlayback> handler in handlers.GetInvocationList())
            try { handler(this); }
            catch (Exception exception) { new Logger<CutscenePlayback>().Error(exception.ToString()); }
    }
}

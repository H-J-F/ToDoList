namespace ToDoList.Core;

// A monotonic deadline is also checked on click, independently of dispatcher ticks.
public sealed class UndoWindow(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _started;
    public long Generation { get; private set; }
    public bool HasUndo { get; private set; }
    public bool Accepted { get; private set; }
    public bool EnteredSync { get; private set; }
    public TimeSpan Elapsed => _clock.GetElapsedTime(_started);
    public double Remaining => Math.Clamp(1 - Elapsed.TotalSeconds / 4.5, 0, 1);
    public bool CanUndo => HasUndo && !Accepted && Elapsed < TimeSpan.FromSeconds(4.5);
    public bool ShouldFade => !Accepted && Elapsed >= TimeSpan.FromSeconds(HasUndo ? 5 : 2);
    public void Start(bool undo, bool syncing = false)
    { Generation++; _started = _clock.GetTimestamp(); HasUndo = undo; Accepted = false; EnteredSync = syncing; }
    public void Synced() { if (HasUndo) EnteredSync = true; }
    public bool Accept() { if (!CanUndo) return false; Accepted = true; return true; }
    public void Clear() { Generation++; HasUndo = false; Accepted = false; }
}

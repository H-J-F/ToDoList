namespace ToDoList.Core;

// Scheduling has no UI or network dependency, so time and backoff can be tested directly.
public sealed class CloudSyncSchedule
{
    public static readonly IReadOnlyList<int> Intervals = Array.AsReadOnly(new[] { 5, 10, 15, 20, 25, 30, 60 });
    public static int Normalize(int minutes) => Intervals.Contains(minutes) ? minutes : 5;
    public DateTime NextPoll { get; private set; } = DateTime.MinValue;
    public DateTime NextAttempt { get; private set; } = DateTime.MinValue;
    public DateTime? DirtyAt { get; private set; }
    public long Generation { get; private set; }
    private int _failures;
    public void Changed(DateTime now) { Generation++; DirtyAt = now; }
    public void CancelSave(DateTime now, int minutes, long generation)
    {
        if (generation != Generation) return;
        Generation++; DirtyAt = null;
        if (NextPoll <= now) NextPoll = now.AddMinutes(Normalize(minutes));
    }
    public void Request(DateTime now) { NextPoll = now; }
    public bool Due(DateTime now, bool enabled) => enabled && now >= NextAttempt && now >= NextPoll;
    public void Completed(DateTime now, int minutes, DateTime? dirtyAtStart, bool deferred)
    {
        _failures = 0; NextAttempt = DateTime.MinValue; NextPoll = now.AddMinutes(Normalize(minutes));
        if (!deferred && DirtyAt == dirtyAtStart) DirtyAt = null;
    }
    public void Completed(DateTime now, int minutes, long generation, bool deferred)
    {
        _failures = 0; NextAttempt = DateTime.MinValue; NextPoll = now.AddMinutes(Normalize(minutes));
        if (!deferred && Generation == generation) DirtyAt = null;
    }
    public void EditingEnded(DateTime now) { }
    public void Failed(DateTime now, int minutes = 5) { NextPoll = now.AddMinutes(Normalize(minutes)); NextAttempt = now.AddSeconds(Math.Min(300, 5 * Math.Pow(3, Math.Min(4, _failures++)))); }
    public void Reset() { Generation++; _failures = 0; DirtyAt = null; NextPoll = NextAttempt = DateTime.MinValue; }
}

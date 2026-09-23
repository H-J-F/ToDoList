using System.Runtime.InteropServices;

namespace ToDoList.App.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly string _suffix;
    public SingleInstanceCoordinator(string suffix = "") => _suffix = suffix;
    private const string MutexName = @"Local\ToDoList.Desktop.Singleton.v2";
    private const string EventName = @"Local\ToDoList.Desktop.Activate.v2";
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private Task? _listener;
    private bool _ownsMutex;

    public bool TryAcquire()
    {
        _mutex = new Mutex(true, MutexName + _suffix, out var created);
        return _ownsMutex = created;
    }

    public void Listen(Action activate)
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName + _suffix);
        _listener = Task.Run(() =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var signaled = WaitHandle.WaitAny([_activateEvent, _stop.Token.WaitHandle]);
                if (signaled == 1) break;
                activate();
            }
        });
    }

    public static bool ActivateExisting()
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(EventName);
                AllowSetForegroundWindow(-1);
                signal.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
        }
        return false;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener?.Wait(500); } catch (AggregateException) { }
        _activateEvent?.Dispose();
        _stop.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}

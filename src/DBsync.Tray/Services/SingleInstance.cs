using System.Threading;

namespace DBsync.Tray.Services;

/// <summary>
/// Keeps one tray app per logon session.
/// <para>
/// The handle names are session-scoped rather than global on purpose: two people signed in to the
/// same machine each get their own tray, because a flyout, its toasts and its windows belong to a
/// desktop. The service is the machine-wide part; the UI is not.
/// </para>
/// <para>
/// A second launch is treated as "show me the app" rather than an error. That is what the user
/// meant by double-clicking the shortcut, and it is the behaviour that makes a tray-only app
/// discoverable at all — the alternative is a launch that appears to do nothing.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\DBsync.Tray.Instance";
    private const string SignalName = @"Local\DBsync.Tray.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _stopping = new();

    private SingleInstance(Mutex mutex, EventWaitHandle signal)
    {
        _mutex = mutex;
        _signal = signal;
    }

    /// <summary>Another launch asked this instance to show itself. Raised off the UI thread.</summary>
    public event Action? ShowRequested;

    /// <summary>
    /// Claims the slot for this session, or returns null when another tray app already holds it —
    /// having first asked that one to show its flyout.
    /// </summary>
    public static SingleInstance? Claim()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);

        if (createdNew) return new SingleInstance(mutex, signal);

        signal.Set();
        mutex.Dispose();
        signal.Dispose();
        return null;
    }

    /// <summary>
    /// Starts answering later launches. Called once the flyout exists, so a signal that arrives
    /// immediately has something to open.
    /// </summary>
    public void ListenForOtherLaunches()
    {
        var thread = new Thread(WaitLoop)
        {
            IsBackground = true,
            Name = "DBsync single-instance",
        };

        thread.Start();
    }

    private void WaitLoop()
    {
        var handles = new WaitHandle[] { _signal, _stopping.Token.WaitHandle };

        // Index 0 is another launch; index 1 is our own shutdown, which ends the loop.
        while (WaitHandle.WaitAny(handles) == 0) ShowRequested?.Invoke();
    }

    public void Dispose()
    {
        _stopping.Cancel();

        // Releasing throws when this process never owned it, which cannot happen for an instance
        // that exists — but disposing must not be the thing that takes the app down on the way out.
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _mutex.Dispose();
        _signal.Dispose();
        _stopping.Dispose();
    }
}

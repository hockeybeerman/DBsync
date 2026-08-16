namespace DBsync.Service.Engine;

/// <summary>
/// An awaitable open/closed gate. Pausing has to stop work between files without tearing the
/// loop down, so the loop awaits the gate rather than checking a flag and spinning.
/// </summary>
public sealed class AsyncGate : IDisposable
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _opened =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AsyncGate(bool open)
    {
        if (open) _opened.TrySetResult(true);
    }

    public bool IsOpen
    {
        get { lock (_sync) return _opened.Task.IsCompleted; }
    }

    public void Open()
    {
        lock (_sync) _opened.TrySetResult(true);
    }

    public void Close()
    {
        lock (_sync)
        {
            if (_opened.Task.IsCompleted)
                _opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public Task WaitAsync(CancellationToken ct)
    {
        Task gate;
        lock (_sync) gate = _opened.Task;

        return gate.IsCompleted ? Task.CompletedTask : gate.WaitAsync(ct);
    }

    public void Dispose() => Open();
}

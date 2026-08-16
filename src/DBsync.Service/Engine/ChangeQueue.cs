using System.Collections.Concurrent;
using System.Diagnostics;

namespace DBsync.Service.Engine;

/// <summary>
/// Coalesces filesystem events into one reconcile per path. Editors write a file several times
/// in quick succession (temp, rename, attribute touch), and a save inside a large tree can fan
/// out to hundreds of events — without a quiet period the engine would copy the same file
/// repeatedly, mid-write.
/// </summary>
public sealed class ChangeQueue : IDisposable
{
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<IReadOnlyCollection<string>> _flush;
    private readonly Timer _timer;
    private long _debounceTicks;

    public ChangeQueue(int debounceMilliseconds, Action<IReadOnlyCollection<string>> flush)
    {
        _flush = flush;
        _debounceTicks = TicksFor(debounceMilliseconds);

        var interval = Math.Max(100, debounceMilliseconds / 2);
        _timer = new Timer(_ => Drain(), null, interval, interval);
    }

    public int PendingCount => _pending.Count;

    public void SetDebounce(int debounceMilliseconds) =>
        Interlocked.Exchange(ref _debounceTicks, TicksFor(debounceMilliseconds));

    /// <summary>Records a touch. Re-touching a path restarts its quiet period.</summary>
    public void Enqueue(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return;
        _pending[relativePath] = _clock.ElapsedTicks;
    }

    /// <summary>Releases every pending path immediately, ignoring the quiet period.</summary>
    public void FlushNow() => Drain(force: true);

    private void Drain(bool force = false)
    {
        if (_pending.IsEmpty) return;

        var cutoff = _clock.ElapsedTicks - Interlocked.Read(ref _debounceTicks);
        var matured = new List<string>();

        foreach (var entry in _pending)
        {
            if (!force && entry.Value > cutoff) continue;
            if (_pending.TryRemove(entry.Key, out _)) matured.Add(entry.Key);
        }

        if (matured.Count > 0) _flush(matured);
    }

    private static long TicksFor(int milliseconds) =>
        (long)(Math.Max(0, milliseconds) / 1000.0 * Stopwatch.Frequency);

    public void Dispose() => _timer.Dispose();
}

using System.Diagnostics;

namespace DBsync.Service.Engine;

/// <summary>
/// Token-bucket throttle for outbound bytes, one per pair, backing the wizard's "Upload limit"
/// field. A null or non-positive limit disables it entirely.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastTicks;

    public RateLimiter(long? bytesPerSecond) => SetLimit(bytesPerSecond);

    public bool Enabled => Volatile.Read(ref _bytesPerSecond) > 0;

    public void SetLimit(long? bytesPerSecond)
    {
        lock (_gate)
        {
            _bytesPerSecond = bytesPerSecond is > 0 ? bytesPerSecond.Value : 0;
            // Start with one second of burst so small files are not delayed on the first write.
            _tokens = _bytesPerSecond;
            _lastTicks = _clock.ElapsedTicks;
        }
    }

    /// <summary>Blocks until <paramref name="bytes"/> may be written, refilling the bucket as time passes.</summary>
    public async Task ConsumeAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                if (_bytesPerSecond <= 0) return;

                var now = _clock.ElapsedTicks;
                var elapsedSeconds = (now - _lastTicks) / (double)Stopwatch.Frequency;
                _lastTicks = now;
                _tokens = Math.Min(_bytesPerSecond, _tokens + elapsedSeconds * _bytesPerSecond);

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                var deficit = (bytes - _tokens) / _bytesPerSecond;
                wait = TimeSpan.FromSeconds(Math.Min(deficit, 1.0));
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Parses the wizard's free-text limit — "8 MB/s", "512KB", "1.5 mb/s" — into bytes per
    /// second. Returns null for blank input or "unlimited".
    /// </summary>
    public static long? ParseLimit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim().ToLowerInvariant().Replace("/s", "").Replace("ps", "").Trim();
        if (value is "0" or "none" or "unlimited") return null;

        var unit = 1L;
        if (value.EndsWith("gb")) { unit = 1024L * 1024 * 1024; value = value[..^2]; }
        else if (value.EndsWith("mb")) { unit = 1024L * 1024; value = value[..^2]; }
        else if (value.EndsWith("kb")) { unit = 1024L; value = value[..^2]; }
        else if (value.EndsWith("b")) { value = value[..^1]; }

        return double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0
            ? (long)(number * unit)
            : null;
    }

    /// <summary>Renders a limit back into the wizard's format.</summary>
    public static string FormatLimit(long? bytesPerSecond)
    {
        if (bytesPerSecond is not > 0) return "Unlimited";

        var bytes = bytesPerSecond.Value;
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024d / 1024 / 1024:0.#} GB/s";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024d / 1024:0.#} MB/s";
        if (bytes >= 1024) return $"{bytes / 1024d:0.#} KB/s";
        return $"{bytes} B/s";
    }
}

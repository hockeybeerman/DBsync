using DBsync.Contracts;

namespace DBsync.Service.Configuration;

/// <summary>The persisted shape of <c>config.json</c>.</summary>
public sealed class ServiceConfig
{
    /// <summary>Schema version, so a future release can migrate rather than guess.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Global pause. Survives service restarts, which is what the flyout expects.</summary>
    public bool PausedAll { get; set; }

    public List<FolderPair> Pairs { get; set; } = new();

    /// <summary>Quiet period after the last filesystem event before a path is reconciled.</summary>
    public int DebounceMilliseconds { get; set; } = 750;

    /// <summary>Full-tree reconcile interval, the safety net behind the watcher.</summary>
    public int SweepSeconds { get; set; } = 300;

    /// <summary>Backoff ladder (seconds) used while a destination is unreachable.</summary>
    public List<int> RetrySecondsLadder { get; set; } = new() { 5, 10, 20, 40, 60, 120, 300 };

    /// <summary>Attempts per file before a copy is logged as failed.</summary>
    public int CopyAttempts { get; set; } = 3;

    /// <summary>Rows kept in the activity log; older rows are trimmed on startup and daily.</summary>
    public int ActivityRetentionDays { get; set; } = 30;
}

namespace DBsync.Contracts;

/// <summary>Live status of a folder pair. Maps 1:1 to the tray flyout status column.</summary>
public enum PairStatus
{
    /// <summary>A transfer batch is in flight; <see cref="FolderPair.Percent"/> is meaningful.</summary>
    Syncing,

    /// <summary>Both sides match and the watcher is idle.</summary>
    InSync,

    /// <summary>At least one unresolved conflict is parked for this pair.</summary>
    Conflict,

    /// <summary>The destination is unreachable; the worker is in retry backoff.</summary>
    Waiting,

    /// <summary>Global pause is in effect, or the pair was individually disabled.</summary>
    Paused,
}

/// <summary>Which way changes are allowed to flow.</summary>
public enum SyncDirection
{
    /// <summary>Changes flow in both directions; simultaneous edits raise a conflict.</summary>
    TwoWay,

    /// <summary>Local wins; the share mirrors this PC.</summary>
    Push,

    /// <summary>Share wins; this PC mirrors it.</summary>
    Pull,
}

/// <summary>How the destination was addressed when the pair was created.</summary>
public enum DestinationKind
{
    /// <summary>A UNC path such as <c>\\NAS-01\team\projects</c>.</summary>
    Unc,

    /// <summary>A mapped drive path such as <c>Z:\team\projects</c>.</summary>
    Drive,
}

/// <summary>Event categories recorded in the activity log.</summary>
public enum ActivityEventKind
{
    Sent,
    Received,
    Conflict,
    Deleted,
    Retry,
    Locked,
}

/// <summary>Outcome tag shown in the activity log's Result column.</summary>
public enum ActivityResult
{
    Ok,
    Pending,
    Offline,
    Skipped,
    Failed,
}

/// <summary>Choice made in the conflict dialog.</summary>
public enum ConflictResolution
{
    /// <summary>The copy on this PC wins; it is pushed to the share.</summary>
    KeepLocal,

    /// <summary>The network copy wins; it is pulled down to this PC.</summary>
    KeepShare,

    /// <summary>The local copy is renamed and both files survive on both sides.</summary>
    KeepBoth,
}

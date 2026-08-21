namespace DBsync.Contracts;

/// <summary>Everything the tray flyout needs for a full repaint.</summary>
public sealed class ServiceState
{
    public List<FolderPair> Pairs { get; set; } = new();

    /// <summary>Global pause. Overrides every row's displayed status.</summary>
    public bool PausedAll { get; set; }

    /// <summary>
    /// Header status line, already resolved to the exact product copy:
    /// "Everything in sync", "2 files need your attention", or "All syncing paused".
    /// </summary>
    public string StatusLine { get; set; } = "Everything in sync";

    /// <summary>True while any pair is transferring — drives the rotating tray glyph.</summary>
    public bool AnySyncing { get; set; }

    /// <summary>Service build version, so the tray app can warn on a protocol mismatch.</summary>
    public string ServiceVersion { get; set; } = "";

    /// <summary>
    /// Paths waiting to be reconciled across every pair. Backs the resume toast's "Catching up on
    /// N queued changes" — without it that number would have to be invented.
    /// </summary>
    public int QueuedChanges { get; set; }

    /// <summary>
    /// The Windows account the service is logged on as, e.g. "NT AUTHORITY\SYSTEM" or
    /// "CONTOSO\alice".
    /// <para>
    /// This is the identity a share actually sees when no credentials are stored for a pair, so
    /// the wizard can say whose access is being used rather than leaving the user to guess. It is
    /// the service's own identity, not the signed-in user's - the two are usually different.
    /// </para>
    /// </summary>
    public string ServiceAccount { get; set; } = "";
}

/// <summary>One row of the activity &amp; history table.</summary>
public sealed class ActivityEntry
{
    public long Id { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public ActivityEventKind Kind { get; set; }

    /// <summary>Path relative to the pair root, as shown in the File column.</summary>
    public string File { get; set; } = "";

    public string PairId { get; set; } = "";

    /// <summary>Denormalised pair name so the log survives pair deletion.</summary>
    public string PairName { get; set; } = "";

    public ActivityResult Result { get; set; }

    /// <summary>Optional failure text (Win32 message, exception summary).</summary>
    public string? Message { get; set; }

    public long Bytes { get; set; }
}

/// <summary>Aggregate counters for the activity window header.</summary>
public sealed class ActivitySummary
{
    public int FilesSent { get; set; }
    public int FilesReceived { get; set; }
    public int Conflicts { get; set; }
    public int PairCount { get; set; }
}

/// <summary>One side of a conflicted file, as rendered on the dialog's selectable cards.</summary>
public sealed class ConflictSide
{
    public DateTimeOffset ModifiedUtc { get; set; }
    public long Bytes { get; set; }

    /// <summary>Owner shown under the timestamp ("dana", "m.reyes"). Best-effort.</summary>
    public string Owner { get; set; } = "";
}

/// <summary>A file that changed on both sides since the last successful sync.</summary>
public sealed class ConflictRecord
{
    public long Id { get; set; }
    public string PairId { get; set; } = "";
    public string PairName { get; set; } = "";

    /// <summary>Path relative to the pair root, e.g. <c>Q3-forecast.xlsx</c>.</summary>
    public string RelativePath { get; set; } = "";

    public string SharePath { get; set; } = "";
    public ConflictSide Local { get; set; } = new();
    public ConflictSide Share { get; set; } = new();
    public DateTimeOffset DetectedUtc { get; set; }
    public bool Resolved { get; set; }
}

/// <summary>Result of probing a destination during the pair wizard's step 2.</summary>
public sealed class DestinationProbe
{
    public bool Reachable { get; set; }
    public bool Writable { get; set; }
    public long FreeBytes { get; set; }

    /// <summary>
    /// Ready-to-render validation copy — "Reachable — 2.1 TB free, write access confirmed"
    /// or the Win32 failure message.
    /// </summary>
    public string Message { get; set; } = "";
}

namespace DBsync.Contracts.Ipc;

// Request payloads ------------------------------------------------------------

public sealed class PairIdRequest
{
    public string PairId { get; set; } = "";
}

public sealed class SetPairEnabledRequest
{
    public string PairId { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class SavePairRequest
{
    public FolderPair Pair { get; set; } = new();

    /// <summary>Optional UNC credentials to file in Credential Manager alongside the save.</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class ActivityQuery
{
    /// <summary>Null means every pair.</summary>
    public string? PairId { get; set; }

    /// <summary>Window to look back over. Defaults to the last 24 hours.</summary>
    public TimeSpan? Since { get; set; }

    public int Limit { get; set; } = 200;

    /// <summary>Skip this many rows — lets the activity window page without holding a cursor.</summary>
    public int Offset { get; set; }

    /// <summary>
    /// Only this kind of event. Null means every kind.
    /// <para>
    /// Filtered in SQL rather than by the caller: the rows are paged, so dropping unwanted ones
    /// after the fact returns short pages and makes "is there more?" unanswerable.
    /// </para>
    /// </summary>
    public ActivityEventKind? Kind { get; set; }

    /// <summary>
    /// Case-insensitive substring matched against the file path and the folder pair's name. Null
    /// or blank means no search.
    /// <para>
    /// Also in SQL, and for a stronger reason than Kind: searching only the rows already fetched
    /// would report "no matches" for a file that is simply further down the history.
    /// </para>
    /// </summary>
    public string? Search { get; set; }
}

public sealed class ConflictQuery
{
    public string? PairId { get; set; }
    public bool IncludeResolved { get; set; }
}

public sealed class ResolveConflictRequest
{
    public long ConflictId { get; set; }
    public ConflictResolution Resolution { get; set; }

    /// <summary>Apply the same choice to every other unresolved conflict in the same pair.</summary>
    public bool ApplyToPair { get; set; }
}

public sealed class ProbeDestinationRequest
{
    public string Path { get; set; } = "";
    public DestinationKind Kind { get; set; } = DestinationKind.Unc;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class StoreCredentialsRequest
{
    public string PairId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

// Response payloads -----------------------------------------------------------

public sealed class PairResponse
{
    public FolderPair Pair { get; set; } = new();
}

public sealed class ActivityResponse
{
    public List<ActivityEntry> Entries { get; set; } = new();
}

public sealed class ConflictsResponse
{
    public List<ConflictRecord> Conflicts { get; set; } = new();
}

public sealed class AckResponse
{
    public bool Ok { get; set; } = true;
    public string? Message { get; set; }
}

// Event payloads --------------------------------------------------------------

public sealed class PairChangedEvent
{
    public FolderPair Pair { get; set; } = new();

    /// <summary>Recomputed header copy, so the tray never has to derive it.</summary>
    public string StatusLine { get; set; } = "";

    public bool AnySyncing { get; set; }
}

public sealed class ReachabilityEvent
{
    public string PairId { get; set; } = "";
    public bool Reachable { get; set; }
    public string Detail { get; set; } = "";

    /// <summary>
    /// The destination refused the stored credentials rather than being absent. Retrying will not
    /// help, so a client should ask for a new sign-in instead of waiting.
    /// </summary>
    public bool NeedsCredentials { get; set; }

    /// <summary>Pair name, so a notification can name it without a second lookup.</summary>
    public string PairName { get; set; } = "";
}

public sealed class LogAppendedEvent
{
    public ActivityEntry Entry { get; set; } = new();
}

public sealed class ConflictRaisedEvent
{
    public ConflictRecord Conflict { get; set; } = new();

    /// <summary>Unresolved conflicts in this pair after the new one landed.</summary>
    public int PendingInPair { get; set; }
}

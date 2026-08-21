namespace DBsync.Tray.Notifications;

/// <summary>What a toast is about, so a click can route back to the right surface.</summary>
public enum ToastKind
{
    PairCreated,
    Paused,
    Resumed,
    ConflictResolved,
    BothCopiesKept,
    ConflictRaised,

    /// <summary>A share refused the stored sign-in.</summary>
    CredentialsNeeded,

    /// <summary>A folder pair was removed.</summary>
    PairRemoved,
}

/// <summary>One notification, ready to render.</summary>
public sealed record ToastMessage(ToastKind Kind, string Title, string Body);

/// <summary>
/// The design's notification copy, in one place.
/// <para>
/// Every string here is transcribed from the table in <c>docs/README.md</c> §5. Keeping them
/// together means the words can be checked against the design in one read, rather than hunting
/// through the call sites that raise them.
/// </para>
/// </summary>
public static class ToastCopy
{
    public static ToastMessage PairCreated(string localPath, string sharePath) =>
        new(ToastKind.PairCreated, "Folder pair created",
            $"{localPath} is now syncing with {sharePath}");

    /// <summary>
    /// Not in the design's table, and the counterpart to PairCreated. It repeats that no files
    /// were touched: the confirmation said so, but the toast is what the user is left looking at.
    /// </summary>
    public static ToastMessage PairRemoved(string pairName) =>
        new(ToastKind.PairRemoved, "Folder pair removed",
            $"{pairName} is no longer syncing. No files were deleted.");

    public static ToastMessage Paused() =>
        new(ToastKind.Paused, "Syncing paused",
            "DBsync will keep watching but send nothing until you resume.");

    /// <summary>
    /// The design's copy is "Catching up on 34 queued changes." The count comes from
    /// <c>ServiceState.QueuedChanges</c> rather than being invented — and when there is genuinely
    /// nothing queued, the sentence is replaced instead of printing "0 queued changes".
    /// </summary>
    public static ToastMessage Resumed(int queuedChanges) =>
        new(ToastKind.Resumed, "Syncing resumed",
            queuedChanges switch
            {
                <= 0 => "DBsync is watching for changes again.",
                1 => "Catching up on 1 queued change.",
                _ => $"Catching up on {queuedChanges:N0} queued changes.",
            });

    public static ToastMessage ConflictResolved(bool keptLocal, string fileName) =>
        new(ToastKind.ConflictResolved, "Conflict resolved",
            $"{(keptLocal ? "The copy on this PC" : "The network copy")} of {fileName} was kept.");

    public static ToastMessage BothCopiesKept(string renamedFileName) =>
        new(ToastKind.BothCopiesKept, "Both copies kept",
            $"Saved as {renamedFileName} alongside the original.");

    /// <summary>
    /// Not in the design's table. A rejected sign-in stops a pair dead and no amount of waiting
    /// fixes it, so the user has to be told — an addition, not transcription.
    /// </summary>
    public static ToastMessage CredentialsNeeded(string pairName) =>
        new(ToastKind.CredentialsNeeded, "Sign-in needed",
            $"{pairName} cannot reach its share — the saved credentials were rejected.");

    public static ToastMessage CredentialsSaved(string pairName) =>
        new(ToastKind.CredentialsNeeded, "Credentials saved",
            $"Trying {pairName} again with the new sign-in.");

    /// <summary>
    /// Not in the design's table. A conflict is the one thing that stops a pair syncing and needs
    /// a person, so it is worth surfacing — but it is an addition, not transcription.
    /// </summary>
    public static ToastMessage ConflictRaised(string fileName, string pairName) =>
        new(ToastKind.ConflictRaised, "Both copies changed",
            $"{fileName} in {pairName} needs you to choose which copy wins.");
}

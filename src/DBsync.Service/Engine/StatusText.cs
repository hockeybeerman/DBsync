using System.Globalization;
using DBsync.Contracts;

namespace DBsync.Service.Engine;

/// <summary>
/// The exact strings the flyout renders. Copy lives on the service side so every client — tray
/// app, CLI, future admin console — shows the same words without re-deriving them.
/// </summary>
public static class StatusText
{
    public const string Paused = "Paused — resume to continue";

    /// <summary>Shown between service start and a pair's first completed pass.</summary>
    public const string StartingUp = "Starting up — checking both folders";

    public static string UpToDate(DateTimeOffset lastChecked) =>
        $"Up to date · checked {Ago(lastChecked)}";

    public static string FirstScan(int fileCount) =>
        $"First scan — comparing {fileCount.ToString("N0", CultureInfo.InvariantCulture)} files";

    public static string Sending(string fileName, int done, int total) =>
        $"Sending {fileName} — {done.ToString("N0", CultureInfo.InvariantCulture)} of " +
        $"{total.ToString("N0", CultureInfo.InvariantCulture)} files";

    public static string Receiving(string fileName, int done, int total) =>
        $"Receiving {fileName} — {done.ToString("N0", CultureInfo.InvariantCulture)} of " +
        $"{total.ToString("N0", CultureInfo.InvariantCulture)} files";

    public static string Conflicts(int count) =>
        count == 1 ? "1 file changed in both places" : $"{count} files changed in both places";

    public static string Unreachable(int retryInSeconds) =>
        $"Share unreachable — retrying in {Math.Max(1, retryInSeconds)}s";

    public static string UnreachableNow() => "Share unreachable — reconnecting";

    /// <summary>
    /// Not in the design, which has no copy for a rejected sign-in. Deliberately does not mention
    /// retrying: retrying is exactly what will not help here.
    /// </summary>
    public static string SignInNeeded() => "Sign-in needed — the saved credentials were rejected";

    /// <summary>Header line above the pair list.</summary>
    /// <param name="signInCount">
    /// Pairs whose sign-in was refused. The design's header counts files, and a rejected sign-in
    /// is not a file — but reporting "Everything in sync" over a pair that has stopped dead is the
    /// one thing the header must never do, so it gets its own wording rather than being folded
    /// into the file count.
    /// </param>
    public static string HeaderLine(bool pausedAll, int attentionCount, int signInCount = 0)
    {
        if (pausedAll) return "All syncing paused";
        if (attentionCount > 0)
            return attentionCount == 1
                ? "1 file needs your attention"
                : $"{attentionCount} files need your attention";

        if (signInCount > 0)
            return signInCount == 1
                ? "1 folder pair needs a sign-in"
                : $"{signInCount} folder pairs need a sign-in";

        return "Everything in sync";
    }

    /// <summary>"just now", "2 minutes ago", "3 hours ago", "yesterday".</summary>
    public static string Ago(DateTimeOffset moment)
    {
        var span = DateTimeOffset.UtcNow - moment;
        if (span < TimeSpan.FromSeconds(45)) return "just now";
        if (span < TimeSpan.FromMinutes(2)) return "1 minute ago";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} minutes ago";
        if (span < TimeSpan.FromHours(2)) return "1 hour ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} hours ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        return $"{(int)span.TotalDays} days ago";
    }

    /// <summary>
    /// The name a losing copy takes under "Keep both" — <c>Q3-forecast (dana, DESKTOP-7L).xlsx</c>.
    /// </summary>
    public static string KeepBothName(string fileName, string user, string machine)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return $"{stem} ({user}, {machine}){extension}";
    }

    /// <summary>Wizard step-2 validation copy for a successful probe.</summary>
    public static string ProbeSuccess(long freeBytes, bool writable) =>
        writable
            ? $"Reachable — {Win32.VolumeInfo.FormatBytes(freeBytes)} free, write access confirmed"
            : $"Reachable — {Win32.VolumeInfo.FormatBytes(freeBytes)} free, but this account cannot write here";

    /// <summary>Counts the rows the header's "needs your attention" number covers.</summary>
    public static int AttentionCount(IEnumerable<FolderPair> pairs) =>
        pairs.Sum(pair => pair.Status == PairStatus.Conflict ? Math.Max(1, pair.PendingConflicts) : 0);

    /// <summary>Pairs stopped because the share refused their stored sign-in.</summary>
    public static int SignInCount(IEnumerable<FolderPair> pairs) =>
        pairs.Count(pair => pair.NeedsCredentials);
}

namespace DBsync.Service.Configuration;

/// <summary>
/// Machine-level storage locations. Everything the service owns lives under
/// <c>%ProgramData%\DBsync</c> — the tray app never touches these files directly, it goes
/// through the pipe.
/// </summary>
public static class ServicePaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DBsync");

    /// <summary>Folder pairs, exclusions, limits, global pause.</summary>
    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>Activity log, conflict store, and the per-file sync baseline.</summary>
    public static string DatabaseFile => Path.Combine(Root, "dbsync.db");

    public static string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>
    /// Per-pair version store. Kept inside the local root so retained versions travel with the
    /// folder and are trivially excluded from sync.
    /// </summary>
    public const string VersionFolderName = ".dbsync-versions";

    /// <summary>Name of the temp file used for atomic writes into the destination.</summary>
    public const string TempSuffix = ".dbsync-part";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogDirectory);
    }
}

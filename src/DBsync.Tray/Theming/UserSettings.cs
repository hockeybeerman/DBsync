using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DBsync.Tray.Theming;

/// <summary>
/// Per-user tray preferences, in the user's own profile.
/// <para>
/// Appearance deliberately lives here and not in the service's machine-level store: the service
/// is shared by every session on the machine, while appearance is one person's choice. This is
/// why there is no <c>appearance</c> field anywhere in the IPC contract.
/// </para>
/// </summary>
public sealed class UserSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DBsync");

    public static string FilePath => Path.Combine(Directory, "tray.json");

    /// <summary>
    /// The process-wide instance. Appearance and the wizard's recent-folder list share one file,
    /// so they must share one object — two loaded copies would overwrite each other on save.
    /// </summary>
    public static UserSettings Current { get; } = Load();

    public AppearanceMode Appearance { get; set; } = AppearanceMode.MatchWindows;

    /// <summary>
    /// Local folders offered under RECENT in the wizard's first step. Per-user like appearance,
    /// and equally not the service's business — it is a memory of what this person browsed to.
    /// </summary>
    public List<string> RecentLocalFolders { get; set; } = new();

    /// <summary>Most-recent-first, de-duplicated case-insensitively, capped at the design's three.</summary>
    public void RememberLocalFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        RecentLocalFolders.RemoveAll(entry => string.Equals(entry, path, StringComparison.OrdinalIgnoreCase));
        RecentLocalFolders.Insert(0, path);

        while (RecentLocalFolders.Count > 3) RecentLocalFolders.RemoveAt(RecentLocalFolders.Count - 1);
    }

    public static UserSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Json) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt preferences file is not worth failing a launch over — take the defaults
            // and let the next save overwrite it.
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Roaming profile locked or full. The choice still applies for this session.
        }
    }
}

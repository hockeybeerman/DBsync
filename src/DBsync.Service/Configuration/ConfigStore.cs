using System.Text.Json;
using System.Text.Json.Serialization;
using DBsync.Contracts;
using Microsoft.Extensions.Logging;

namespace DBsync.Service.Configuration;

/// <summary>
/// Reads and writes the machine-level config. All mutation goes through <see cref="Update"/>
/// so writes stay serialised and atomic — a torn config.json would strand every pair.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger<ConfigStore> _log;
    private readonly object _gate = new();
    private ServiceConfig _config = new();

    public ConfigStore(ILogger<ConfigStore> log)
    {
        _log = log;
        ServicePaths.EnsureCreated();
        Load();
    }

    /// <summary>Raised after any successful write, with the new snapshot.</summary>
    public event Action<ServiceConfig>? Changed;

    /// <summary>A defensive copy — callers may not mutate the live config.</summary>
    public ServiceConfig Snapshot()
    {
        lock (_gate) return Clone(_config);
    }

    public FolderPair? FindPair(string pairId)
    {
        lock (_gate)
        {
            var pair = _config.Pairs.FirstOrDefault(p => p.Id == pairId);
            return pair?.CloneSettings();
        }
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the live config under the lock and persists the
    /// result. Returns the new snapshot.
    /// </summary>
    public ServiceConfig Update(Action<ServiceConfig> mutate)
    {
        ServiceConfig snapshot;
        lock (_gate)
        {
            mutate(_config);
            Save(_config);
            snapshot = Clone(_config);
        }

        Changed?.Invoke(snapshot);
        return snapshot;
    }

    private void Load()
    {
        var path = ServicePaths.ConfigFile;
        if (!File.Exists(path))
        {
            _log.LogInformation("No config at {Path}; starting with an empty pair list.", path);
            _config = new ServiceConfig();
            Save(_config);
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            _config = JsonSerializer.Deserialize<ServiceConfig>(json, Json) ?? new ServiceConfig();
            Normalise(_config);
            _log.LogInformation("Loaded {Count} folder pair(s) from {Path}.", _config.Pairs.Count, path);
        }
        catch (Exception ex)
        {
            // Never start with a half-understood config — park the bad file and continue empty,
            // so a hand-edited typo cannot silently drop pairs on the next successful write.
            var quarantine = path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            _log.LogError(ex, "config.json is unreadable; moving it to {Quarantine}.", quarantine);
            try { File.Move(path, quarantine); } catch { /* best effort */ }
            _config = new ServiceConfig();
        }
    }

    private void Save(ServiceConfig config)
    {
        var path = ServicePaths.ConfigFile;
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, Json));

        if (File.Exists(path))
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        else
            File.Move(temp, path);
    }

    private static void Normalise(ServiceConfig config)
    {
        if (config.DebounceMilliseconds < 50) config.DebounceMilliseconds = 50;
        if (config.SweepSeconds < 30) config.SweepSeconds = 30;
        if (config.CopyAttempts < 1) config.CopyAttempts = 1;
        if (config.RetrySecondsLadder.Count == 0) config.RetrySecondsLadder.Add(30);

        foreach (var pair in config.Pairs)
        {
            if (string.IsNullOrWhiteSpace(pair.Id)) pair.Id = Guid.NewGuid().ToString("n");
            if (pair.VersionsKept < 0) pair.VersionsKept = 0;
            pair.Excludes = pair.Excludes.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        }
    }

    private static ServiceConfig Clone(ServiceConfig config) => new()
    {
        Version = config.Version,
        PausedAll = config.PausedAll,
        Pairs = config.Pairs.Select(p => p.CloneSettings()).ToList(),
        DebounceMilliseconds = config.DebounceMilliseconds,
        SweepSeconds = config.SweepSeconds,
        RetrySecondsLadder = new List<int>(config.RetrySecondsLadder),
        CopyAttempts = config.CopyAttempts,
        ActivityRetentionDays = config.ActivityRetentionDays,
    };
}

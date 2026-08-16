using System.Collections.Concurrent;
using System.Reflection;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Service.Configuration;
using DBsync.Service.Storage;
using DBsync.Service.Win32;
using Microsoft.Extensions.Logging;

namespace DBsync.Service.Engine;

/// <summary>
/// The service's single source of truth. Holds one <see cref="PairWorker"/> per configured pair,
/// applies every mutation the tray app requests, and republishes state changes as events.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly ConfigStore _config;
    private readonly Database _database;
    private readonly SyncStateStore _baselines;
    private readonly ConflictStore _conflicts;
    private readonly ActivityLog _activity;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SyncEngine> _log;

    private readonly ConcurrentDictionary<string, PairWorker> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public SyncEngine(
        ConfigStore config,
        Database database,
        SyncStateStore baselines,
        ConflictStore conflicts,
        ActivityLog activity,
        ILoggerFactory loggerFactory,
        ILogger<SyncEngine> log)
    {
        _config = config;
        _database = database;
        _baselines = baselines;
        _conflicts = conflicts;
        _activity = activity;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    /// <summary>Fires whenever any observable state moves; the IPC server turns these into frames.</summary>
    public event Action<IpcEventKind, object>? Published;

    public void Start()
    {
        var config = _config.Snapshot();
        foreach (var pair in config.Pairs) Launch(pair, config);

        _activity.Trim(config.ActivityRetentionDays);
        _log.LogInformation("Sync engine started with {Count} pair(s); global pause is {Paused}.",
            config.Pairs.Count, config.PausedAll);
    }

    public async Task StopAsync()
    {
        foreach (var worker in _workers.Values) await worker.StopAsync().ConfigureAwait(false);
        _workers.Clear();
    }

    // ---- State --------------------------------------------------------------

    public ServiceState GetState()
    {
        var config = _config.Snapshot();
        var pairs = config.Pairs
            .Select(pair => _workers.TryGetValue(pair.Id, out var worker) ? worker.Snapshot() : Idle(pair))
            .ToList();

        return new ServiceState
        {
            Pairs = pairs,
            PausedAll = config.PausedAll,
            StatusLine = StatusText.HeaderLine(config.PausedAll, StatusText.AttentionCount(pairs)),
            AnySyncing = !config.PausedAll && pairs.Any(pair => pair.Status == PairStatus.Syncing),
            ServiceVersion = Version,
        };
    }

    private FolderPair Idle(FolderPair pair)
    {
        var projection = pair.CloneSettings();
        projection.Status = pair.Enabled ? PairStatus.Waiting : PairStatus.Paused;
        projection.PendingConflicts = _conflicts.CountOpen(pair.Id);
        projection.Detail = pair.Enabled ? "Starting up" : StatusText.Paused;
        return projection;
    }

    // ---- Mutations ----------------------------------------------------------

    public async Task<FolderPair> AddPairAsync(SavePairRequest request)
    {
        await _mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            var pair = Validate(request.Pair, isNew: true);
            if (string.IsNullOrWhiteSpace(pair.Id)) pair.Id = Guid.NewGuid().ToString("n");

            SaveCredentialsIfSupplied(pair, request.Username, request.Password);

            var config = _config.Update(current => current.Pairs.Add(pair.CloneSettings()));
            Launch(pair, config);

            _log.LogInformation("Added pair {Name} ({Local} <-> {Share}).",
                pair.Name, pair.LocalPath, pair.SharePath);

            Publish(IpcEventKind.StateChanged, GetState());
            return _workers.TryGetValue(pair.Id, out var worker) ? worker.Snapshot() : pair;
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task<FolderPair> UpdatePairAsync(SavePairRequest request)
    {
        await _mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            var incoming = Validate(request.Pair, isNew: false);
            var existing = _config.FindPair(incoming.Id)
                           ?? throw new InvalidOperationException($"No pair with id {incoming.Id}.");

            SaveCredentialsIfSupplied(incoming, request.Username, request.Password);

            var config = _config.Update(current =>
            {
                var index = current.Pairs.FindIndex(p => p.Id == incoming.Id);
                if (index >= 0) current.Pairs[index] = incoming.CloneSettings();
            });

            if (_workers.TryGetValue(incoming.Id, out var worker)) worker.ApplySettings(incoming, config);
            else Launch(incoming, config);

            _log.LogInformation("Updated pair {Name}.", incoming.Name);
            Publish(IpcEventKind.StateChanged, GetState());

            return _workers.TryGetValue(incoming.Id, out var updated) ? updated.Snapshot() : incoming;
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task DeletePairAsync(string pairId)
    {
        await _mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_workers.TryRemove(pairId, out var worker)) await worker.DisposeAsync().ConfigureAwait(false);

            _config.Update(current => current.Pairs.RemoveAll(p => p.Id == pairId));

            // Baseline and open conflicts go; the activity log stays so the history survives.
            _database.ForgetPair(pairId, includeActivity: false);
            CredentialManager.Delete(pairId);

            _log.LogInformation("Removed pair {PairId}.", pairId);
            Publish(IpcEventKind.StateChanged, GetState());
        }
        finally
        {
            _mutation.Release();
        }
    }

    public async Task SetPairEnabledAsync(string pairId, bool enabled)
    {
        var pair = _config.FindPair(pairId) ?? throw new InvalidOperationException($"No pair with id {pairId}.");
        pair.Enabled = enabled;
        await UpdatePairAsync(new SavePairRequest { Pair = pair }).ConfigureAwait(false);
    }

    public ServiceState SetGlobalPause(bool paused)
    {
        _config.Update(current => current.PausedAll = paused);
        foreach (var worker in _workers.Values) worker.SetGlobalPause(paused);

        _log.LogInformation(paused ? "All syncing paused." : "Syncing resumed.");

        var state = GetState();
        Publish(IpcEventKind.StateChanged, state);
        return state;
    }

    public void SyncNow(string? pairId)
    {
        if (string.IsNullOrWhiteSpace(pairId))
        {
            foreach (var worker in _workers.Values) worker.RequestFullScan();
            return;
        }

        if (_workers.TryGetValue(pairId, out var target)) target.RequestFullScan();
        else throw new InvalidOperationException($"No pair with id {pairId}.");
    }

    // ---- Conflicts ----------------------------------------------------------

    public List<ConflictRecord> GetConflicts(ConflictQuery query) => _conflicts.Query(query, _config.FindPair);

    public async Task<AckResponse> ResolveConflictAsync(ResolveConflictRequest request)
    {
        var conflict = _conflicts.Find(request.ConflictId, _config.FindPair)
                       ?? throw new InvalidOperationException($"No conflict with id {request.ConflictId}.");

        if (!_workers.TryGetValue(conflict.PairId, out var worker))
            throw new InvalidOperationException("The pair for this conflict is not running.");

        var targets = new List<ConflictRecord> { conflict };
        if (request.ApplyToPair)
        {
            targets.AddRange(_conflicts.OpenInPair(conflict.PairId, _config.FindPair)
                .Where(other => other.Id != conflict.Id));
        }

        foreach (var target in targets)
            await worker.ResolveAsync(target, request.Resolution).ConfigureAwait(false);

        Publish(IpcEventKind.StateChanged, GetState());

        var message = targets.Count == 1
            ? $"Resolved {conflict.RelativePath}."
            : $"Resolved {targets.Count} conflicts in {conflict.PairName}.";
        return new AckResponse { Ok = true, Message = message };
    }

    // ---- Destination probing & credentials ----------------------------------

    /// <summary>Backs the wizard's step-2 validation line.</summary>
    public DestinationProbe Probe(ProbeDestinationRequest request)
    {
        var path = request.Path?.Trim() ?? "";
        if (path.Length == 0)
            return new DestinationProbe { Message = "Enter a destination path." };

        NetworkConnection? connection = null;
        try
        {
            if (request.Kind == DestinationKind.Unc && !string.IsNullOrEmpty(request.Username))
            {
                connection = NetworkConnection.Attach(path,
                    new NetworkCredentialRecord(request.Username, request.Password ?? ""));
            }

            if (!Directory.Exists(path))
            {
                return new DestinationProbe
                {
                    Message = $"Could not reach {path} — check the path and that the share is online.",
                };
            }

            var free = VolumeInfo.FreeBytes(path);
            var writable = TestWrite(path, out var writeError);

            return new DestinationProbe
            {
                Reachable = true,
                Writable = writable,
                FreeBytes = free,
                Message = writable
                    ? StatusText.ProbeSuccess(free, true)
                    : $"Reachable, but writing failed — {writeError}",
            };
        }
        catch (Exception ex)
        {
            return new DestinationProbe { Message = ex.Message };
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private static bool TestWrite(string path, out string error)
    {
        error = "";
        var probe = Path.Combine(path, ".dbsync-write-probe-" + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { /* probe cleanup is best effort */ }
        }
    }

    public void StoreCredentials(StoreCredentialsRequest request)
    {
        CredentialManager.Write(request.PairId, request.Username, request.Password);

        var pair = _config.FindPair(request.PairId);
        if (pair is { SaveCredentials: false })
        {
            pair.SaveCredentials = true;
            _config.Update(current =>
            {
                var index = current.Pairs.FindIndex(p => p.Id == request.PairId);
                if (index >= 0) current.Pairs[index].SaveCredentials = true;
            });
        }

        // Force a reconnect so the new credentials take effect without a service restart.
        if (_workers.TryGetValue(request.PairId, out var worker)) worker.RequestFullScan();
    }

    // ---- Wiring -------------------------------------------------------------

    private void Launch(FolderPair pair, ServiceConfig config)
    {
        var worker = new PairWorker(pair, config, _baselines, _conflicts, _activity,
            _loggerFactory.CreateLogger($"DBsync.Pair.{pair.Name}"));

        worker.Changed += OnPairChanged;
        worker.ConflictRaised += OnConflictRaised;
        worker.ReachabilityChanged += OnReachabilityChanged;

        _workers[pair.Id] = worker;
        worker.SetGlobalPause(config.PausedAll);
        worker.Start();
    }

    private void OnPairChanged(PairWorker worker)
    {
        var state = GetState();
        Publish(IpcEventKind.PairChanged, new PairChangedEvent
        {
            Pair = worker.Snapshot(),
            StatusLine = state.StatusLine,
            AnySyncing = state.AnySyncing,
        });
    }

    private void OnConflictRaised(ConflictRecord conflict, int pendingInPair) =>
        Publish(IpcEventKind.ConflictRaised,
            new ConflictRaisedEvent { Conflict = conflict, PendingInPair = pendingInPair });

    private void OnReachabilityChanged(PairWorker worker, bool reachable, string detail) =>
        Publish(IpcEventKind.ReachabilityChanged,
            new ReachabilityEvent { PairId = worker.PairId, Reachable = reachable, Detail = detail });

    private void Publish(IpcEventKind kind, object payload)
    {
        try
        {
            Published?.Invoke(kind, payload);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Publishing a {Kind} event failed.", kind);
        }
    }

    private void SaveCredentialsIfSupplied(FolderPair pair, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        CredentialManager.Write(pair.Id, username, password ?? "");
        pair.SaveCredentials = true;
    }

    /// <summary>Rejects configurations the engine cannot service, before anything is persisted.</summary>
    private static FolderPair Validate(FolderPair candidate, bool isNew)
    {
        var pair = candidate.CloneSettings();

        pair.LocalPath = (pair.LocalPath ?? "").Trim();
        pair.SharePath = (pair.SharePath ?? "").Trim();

        if (pair.LocalPath.Length == 0) throw new ArgumentException("A local folder is required.");
        if (pair.SharePath.Length == 0) throw new ArgumentException("A destination path is required.");
        if (!Path.IsPathFullyQualified(pair.LocalPath))
            throw new ArgumentException($"'{pair.LocalPath}' is not an absolute path.");

        pair.LocalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.LocalPath));
        pair.SharePath = Path.TrimEndingDirectorySeparator(pair.SharePath);

        if (Nested(pair.LocalPath, pair.SharePath) || Nested(pair.SharePath, pair.LocalPath))
            throw new ArgumentException("The two folders overlap; a pair would sync into itself.");

        if (string.IsNullOrWhiteSpace(pair.Name))
            pair.Name = Path.GetFileName(pair.LocalPath) is { Length: > 0 } leaf ? leaf : "Folder pair";

        if (!isNew && string.IsNullOrWhiteSpace(pair.Id))
            throw new ArgumentException("An id is required when updating a pair.");

        pair.DestKind = pair.SharePath.StartsWith(@"\\", StringComparison.Ordinal)
            ? DestinationKind.Unc
            : DestinationKind.Drive;

        return pair;
    }

    private static bool Nested(string outer, string inner)
    {
        var prefix = outer.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(outer, inner, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _mutation.Dispose();
    }
}

using System.Threading.Channels;
using DBsync.Contracts;
using DBsync.Service.Configuration;
using DBsync.Service.Storage;
using DBsync.Service.Win32;
using Microsoft.Extensions.Logging;

namespace DBsync.Service.Engine;

/// <summary>
/// Owns one folder pair end to end: its watchers, its debounced change queue, its reconcile
/// loop, and its reachability backoff. Everything that touches the pair's baseline runs on the
/// single loop task, so the engine needs no locking around change detection.
/// </summary>
public sealed class PairWorker : IAsyncDisposable
{
    /// <summary>Timestamps across SMB and FAT round differently; treat sub-2s deltas as equal.</summary>
    private const long TimestampToleranceSeconds = 2;

    private readonly ILogger _log;
    private readonly SyncStateStore _baselineStore;
    private readonly ConflictStore _conflicts;
    private readonly ActivityLog _activity;
    private readonly FileTransfer _transfer;

    private readonly Channel<WorkItem> _work =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResolveCommand> _commands = new();
    private readonly AsyncGate _resumeGate = new(open: true);
    private readonly object _settingsGate = new();

    private FolderPair _settings;
    private ServiceConfig _config;
    private ExclusionMatcher _excludes;
    private RateLimiter _limiter;
    private ChangeQueue _queue;
    private FileSystemWatcher? _localWatcher;
    private FileSystemWatcher? _shareWatcher;
    private NetworkConnection? _connection;
    private Timer? _sweep;

    private Dictionary<string, FileBaseline> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private bool _fullScanRequested = true;
    private bool _globalPause;
    private bool _reachable = true;
    private int _retryIndex;
    private DateTimeOffset _retryAt;

    private PairStatus _status = PairStatus.InSync;
    private int _percent;
    private string? _transientDetail;
    private DateTimeOffset _lastChecked = DateTimeOffset.UtcNow;
    private int _pendingConflicts;
    private int _batchDone;
    private int _batchTotal;

    /// <summary>
    /// False until the first pass completes. Probing an unreachable UNC path can block for tens
    /// of seconds, and a pair that has never compared its two folders must not claim to be in
    /// sync during that window.
    /// </summary>
    private bool _settled;

    public PairWorker(
        FolderPair settings,
        ServiceConfig config,
        SyncStateStore baselineStore,
        ConflictStore conflicts,
        ActivityLog activity,
        ILogger log)
    {
        _settings = settings.CloneSettings();
        _config = config;
        _baselineStore = baselineStore;
        _conflicts = conflicts;
        _activity = activity;
        _log = log;
        _transfer = new FileTransfer(log);
        _excludes = new ExclusionMatcher(_settings.Excludes);
        _limiter = new RateLimiter(_settings.UploadLimitBytesPerSecond);
        _queue = new ChangeQueue(config.DebounceMilliseconds, OnDebounced);
    }

    public string PairId => _settings.Id;

    /// <summary>Raised whenever the row's status, percent, or detail line moves.</summary>
    public event Action<PairWorker>? Changed;

    /// <summary>Raised when a new conflict is parked, so the IPC layer can push a toast.</summary>
    public event Action<ConflictRecord, int>? ConflictRaised;

    /// <summary>Raised when the destination goes offline or comes back.</summary>
    public event Action<PairWorker, bool, string>? ReachabilityChanged;

    // ---- Public surface -----------------------------------------------------

    public FolderPair Snapshot()
    {
        FolderPair projection;
        lock (_settingsGate) projection = _settings.CloneSettings();

        projection.PendingConflicts = _pendingConflicts;
        projection.Status = EffectiveStatus();
        projection.Percent = projection.Status == PairStatus.Syncing ? _percent : 0;
        projection.Detail = DescribeDetail(projection.Status);
        return projection;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _baseline = _baselineStore.Load(_settings.Id);
        _pendingConflicts = _conflicts.CountOpen(_settings.Id);

        StartWatchers();
        StartSweep();

        _loop = Task.Run(() => RunAsync(_cts.Token));
        _log.LogInformation("Pair {Name} started ({Local} <-> {Share}).",
            _settings.Name, _settings.LocalPath, _settings.SharePath);
    }

    public void RequestFullScan()
    {
        _fullScanRequested = true;
        _work.Writer.TryWrite(WorkItem.Wake);
    }

    public void SetGlobalPause(bool paused)
    {
        _globalPause = paused;
        UpdateGate();
        Changed?.Invoke(this);
    }

    /// <summary>Swaps in edited settings without dropping the pair's baseline or its queue.</summary>
    public void ApplySettings(FolderPair updated, ServiceConfig config)
    {
        var rootsMoved = !PathsEqual(updated.LocalPath, _settings.LocalPath)
                         || !PathsEqual(updated.SharePath, _settings.SharePath);

        lock (_settingsGate)
        {
            _settings = updated.CloneSettings();
            _config = config;
        }

        _excludes = new ExclusionMatcher(updated.Excludes);
        _limiter.SetLimit(updated.UploadLimitBytesPerSecond);
        _queue.SetDebounce(config.DebounceMilliseconds);

        UpdateGate();
        StartWatchers();
        StartSweep();

        if (rootsMoved)
        {
            // A repointed pair shares nothing with its old baseline; keeping it would read as
            // "everything was deleted on both sides".
            _baseline.Clear();
            _baselineStore.Clear(updated.Id);
        }

        RequestFullScan();
        Changed?.Invoke(this);
    }

    /// <summary>Queues a conflict decision onto the loop and awaits its outcome.</summary>
    public Task<bool> ResolveAsync(ConflictRecord conflict, ConflictResolution resolution)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Writer.TryWrite(new WorkItem(WorkKind.Resolve,
            Command: new ResolveCommand(conflict, resolution, completion)));
        return completion.Task;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _sweep?.Dispose();
        _queue.Dispose();
        DisposeWatchers();

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _connection?.Dispose();
        _connection = null;
        _log.LogInformation("Pair {Name} stopped.", _settings.Name);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _resumeGate.Dispose();
    }

    // ---- Loop ---------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _resumeGate.WaitAsync(ct).ConfigureAwait(false);
                await AbsorbAsync(ct).ConfigureAwait(false);

                if (!await EnsureReachableAsync(ct).ConfigureAwait(false))
                {
                    await BackoffAsync(ct).ConfigureAwait(false);
                    continue;
                }

                await RunCommandsAsync(ct).ConfigureAwait(false);

                if (_fullScanRequested)
                {
                    _fullScanRequested = false;
                    EnumerateInto(_dirty);
                }

                if (_dirty.Count > 0) await ProcessBatchAsync(ct).ConfigureAwait(false);
                else Settle();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pair {Name} loop faulted; the pair is now idle until the service restarts.",
                _settings.Name);
            _transientDetail = "Sync stopped — see the service log";
            Changed?.Invoke(this);
        }
    }

    /// <summary>Pulls queued work into the pending set, blocking when there is nothing to do.</summary>
    private async Task AbsorbAsync(CancellationToken ct)
    {
        if (_dirty.Count == 0 && _commands.Count == 0 && !_fullScanRequested)
        {
            var first = await _work.Reader.ReadAsync(ct).ConfigureAwait(false);
            Absorb(first);
        }

        while (_work.Reader.TryRead(out var item)) Absorb(item);
    }

    private void Absorb(WorkItem item)
    {
        switch (item.Kind)
        {
            case WorkKind.Reconcile when item.RelativePath is { Length: > 0 } path:
                _dirty.Add(path);
                break;
            case WorkKind.FullScan:
                _fullScanRequested = true;
                break;
            case WorkKind.Resolve when item.Command is not null:
                _commands.Add(item.Command);
                break;
        }
    }

    private void OnDebounced(IReadOnlyCollection<string> paths)
    {
        foreach (var path in paths) _work.Writer.TryWrite(WorkItem.Reconcile(path));
    }

    // ---- Reachability -------------------------------------------------------

    private async Task<bool> EnsureReachableAsync(CancellationToken ct)
    {
        var settings = CurrentSettings();

        if (!Directory.Exists(settings.LocalPath))
        {
            try
            {
                Directory.CreateDirectory(settings.LocalPath);
            }
            catch (Exception ex)
            {
                SetUnreachable($"Local folder unavailable — {ex.Message}");
                return false;
            }
        }

        try
        {
            EnsureConnection(settings);
        }
        catch (Exception ex)
        {
            SetUnreachable(ex.Message);
            return false;
        }

        if (!Directory.Exists(settings.SharePath))
        {
            // A share that vanished mid-session may just be a dropped session; drop the
            // connection so the next attempt re-authenticates from scratch.
            _connection?.Dispose();
            _connection = null;
            SetUnreachable(StatusText.UnreachableNow());
            return false;
        }

        if (!_reachable)
        {
            _reachable = true;
            _retryIndex = 0;
            _transientDetail = null;
            _log.LogInformation("Pair {Name}: destination is reachable again.", settings.Name);
            ReachabilityChanged?.Invoke(this, true, "Destination reachable");
            RequestFullScan();
            Changed?.Invoke(this);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    private void EnsureConnection(FolderPair settings)
    {
        if (_connection is not null) return;
        if (settings.DestKind != DestinationKind.Unc || !settings.SaveCredentials) return;

        var credentials = CredentialManager.Read(settings.Id);
        if (credentials is null) return;

        _connection = NetworkConnection.Attach(settings.SharePath, credentials);
    }

    private void SetUnreachable(string message)
    {
        var wasReachable = _reachable;
        _reachable = false;

        var ladder = _config.RetrySecondsLadder;
        var seconds = ladder[Math.Min(_retryIndex, ladder.Count - 1)];
        _retryAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        _transientDetail = null; // recomputed from _retryAt so the countdown stays live

        if (wasReachable)
        {
            _log.LogWarning("Pair {Name}: destination unreachable — {Message}", _settings.Name, message);
            _activity.Append(_settings.Id, _settings.Name, ActivityEventKind.Retry,
                _settings.SharePath, ActivityResult.Offline, message: message);
            ReachabilityChanged?.Invoke(this, false, message);
        }

        Changed?.Invoke(this);
    }

    private async Task BackoffAsync(CancellationToken ct)
    {
        var remaining = _retryAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.FromSeconds(1);

        // Wake once a second so the "retrying in Ns" countdown on the row stays truthful.
        var deadline = DateTimeOffset.UtcNow + remaining;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            Changed?.Invoke(this);
        }

        _retryIndex = Math.Min(_retryIndex + 1, _config.RetrySecondsLadder.Count - 1);
    }

    // ---- Batch processing ---------------------------------------------------

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        var settings = CurrentSettings();
        var batch = _dirty.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var skipPaths = _conflicts.OpenPaths(settings.Id);

        _batchTotal = batch.Count;
        _batchDone = 0;
        _status = PairStatus.Syncing;
        _percent = 0;
        _transientDetail = _baseline.Count == 0 ? StatusText.FirstScan(batch.Count) : null;
        Changed?.Invoke(this);

        foreach (var relativePath in batch)
        {
            ct.ThrowIfCancellationRequested();
            await _resumeGate.WaitAsync(ct).ConfigureAwait(false);

            if (!skipPaths.Contains(relativePath))
            {
                try
                {
                    await ReconcileAsync(settings, relativePath, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Pair {Name}: reconciling {Path} failed.", settings.Name, relativePath);
                    _activity.Append(settings.Id, settings.Name, ActivityEventKind.Sent, relativePath,
                        ActivityResult.Failed, message: ex.Message);
                }
            }

            _dirty.Remove(relativePath);
            _batchDone++;
            _percent = (int)(_batchDone * 100L / Math.Max(1, _batchTotal));
        }

        Settle();
    }

    /// <summary>Recomputes the resting status once a batch drains.</summary>
    private void Settle()
    {
        _pendingConflicts = _conflicts.CountOpen(_settings.Id);
        _percent = 0;
        _batchDone = 0;
        _batchTotal = 0;
        _transientDetail = null;
        _lastChecked = DateTimeOffset.UtcNow;
        _settled = true;
        _status = _pendingConflicts > 0 ? PairStatus.Conflict : PairStatus.InSync;
        Changed?.Invoke(this);
    }

    // ---- Reconcile ----------------------------------------------------------

    private async Task ReconcileAsync(FolderPair settings, string relativePath, CancellationToken ct)
    {
        if (_excludes.IsExcluded(relativePath))
        {
            if (_baseline.Remove(relativePath)) _baselineStore.Remove(settings.Id, relativePath);
            return;
        }

        var localPath = Path.Combine(settings.LocalPath, relativePath);
        var sharePath = Path.Combine(settings.SharePath, relativePath);

        // Directories only need their counterpart to exist; their contents arrive as their own
        // reconcile items.
        if (Directory.Exists(localPath) || Directory.Exists(sharePath))
        {
            MirrorDirectory(settings, localPath, sharePath);
            return;
        }

        var local = SideInfo.Read(localPath);
        var share = SideInfo.Read(sharePath);

        if (!_baseline.TryGetValue(relativePath, out var baseline))
            baseline = new FileBaseline { RelativePath = relativePath };

        var localChanged = !local.Matches(baseline.LocalBytes, baseline.LocalModified);
        var shareChanged = !share.Matches(baseline.ShareBytes, baseline.ShareModified);

        if (!localChanged && !shareChanged) return;

        // Both gone: the file is history on both sides.
        if (!local.Exists && !share.Exists)
        {
            if (_baseline.Remove(relativePath)) _baselineStore.Remove(settings.Id, relativePath);
            _conflicts.Withdraw(settings.Id, relativePath);
            return;
        }

        // Same content on both sides (a copy the engine did not make, or one it made before a
        // crash): adopt it as the new baseline rather than moving bytes.
        if (local.Exists && share.Exists && local.SameContentAs(share))
        {
            StoreBaseline(settings.Id, relativePath, local, share);
            return;
        }

        var direction = settings.Direction;

        if (localChanged && shareChanged && direction == SyncDirection.TwoWay)
        {
            if (local.Exists && share.Exists)
            {
                RaiseConflict(settings, relativePath, localPath, sharePath, local, share);
                return;
            }

            // Edit on one side, delete on the other. Resurrect the edited copy — losing an edit
            // is worse than resurrecting a file the user can delete again.
            if (local.Exists) await SendAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
            else await ReceiveAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
            return;
        }

        var localWins = direction switch
        {
            SyncDirection.Push => true,
            SyncDirection.Pull => false,
            _ => localChanged,
        };

        if (direction == SyncDirection.Pull && !shareChanged) return;   // local edits are not propagated
        if (direction == SyncDirection.Push && !localChanged) return;   // share edits are not propagated

        if (localWins)
        {
            if (local.Exists) await SendAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
            else DeleteRemote(settings, relativePath, sharePath);
        }
        else
        {
            if (share.Exists) await ReceiveAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
            else DeleteLocal(settings, relativePath, localPath);
        }
    }

    private void MirrorDirectory(FolderPair settings, string localPath, string sharePath)
    {
        try
        {
            if (Directory.Exists(localPath) && !Directory.Exists(sharePath) &&
                settings.Direction != SyncDirection.Pull)
            {
                Directory.CreateDirectory(sharePath);
            }
            else if (Directory.Exists(sharePath) && !Directory.Exists(localPath) &&
                     settings.Direction != SyncDirection.Push)
            {
                Directory.CreateDirectory(localPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Could not mirror directory {Local} / {Share}.", localPath, sharePath);
        }
    }

    private async Task SendAsync(FolderPair settings, string relativePath, string localPath, string sharePath,
        CancellationToken ct)
    {
        AnnounceTransfer(StatusText.Sending(Path.GetFileName(relativePath), _batchDone + 1, Math.Max(1, _batchTotal)));

        var result = await _transfer.CopyAsync(localPath, sharePath, settings.SharePath, _limiter,
            settings.UseVss, settings.VersionsKept, _config.CopyAttempts, null, ct).ConfigureAwait(false);

        RecordTransfer(settings, ActivityEventKind.Sent, relativePath, localPath, sharePath, result);
    }

    private async Task ReceiveAsync(FolderPair settings, string relativePath, string localPath, string sharePath,
        CancellationToken ct)
    {
        AnnounceTransfer(StatusText.Receiving(Path.GetFileName(relativePath), _batchDone + 1, Math.Max(1, _batchTotal)));

        // Inbound copies are not throttled — the limit in the wizard is an upload limit.
        var result = await _transfer.CopyAsync(sharePath, localPath, settings.LocalPath, null,
            settings.UseVss, settings.VersionsKept, _config.CopyAttempts, null, ct).ConfigureAwait(false);

        RecordTransfer(settings, ActivityEventKind.Received, relativePath, localPath, sharePath, result);
    }

    private void RecordTransfer(FolderPair settings, ActivityEventKind kind, string relativePath,
        string localPath, string sharePath, TransferResult result)
    {
        switch (result.Status)
        {
            case TransferStatus.Copied:
                StoreBaseline(settings.Id, relativePath, SideInfo.Read(localPath), SideInfo.Read(sharePath));
                _activity.Append(settings.Id, settings.Name, kind, relativePath, ActivityResult.Ok, result.Bytes);
                break;

            case TransferStatus.Locked:
                _activity.Append(settings.Id, settings.Name, ActivityEventKind.Locked, relativePath,
                    ActivityResult.Skipped, message: result.Message);
                break;

            default:
                _activity.Append(settings.Id, settings.Name, kind, relativePath,
                    ActivityResult.Failed, message: result.Message);
                break;
        }
    }

    private void DeleteRemote(FolderPair settings, string relativePath, string sharePath)
    {
        if (_transfer.Remove(sharePath, settings.SharePath, settings.VersionsKept, out var message))
        {
            _baseline.Remove(relativePath);
            _baselineStore.Remove(settings.Id, relativePath);
            _activity.Append(settings.Id, settings.Name, ActivityEventKind.Deleted, relativePath, ActivityResult.Ok);
        }
        else
        {
            _activity.Append(settings.Id, settings.Name, ActivityEventKind.Deleted, relativePath,
                ActivityResult.Failed, message: message);
        }
    }

    private void DeleteLocal(FolderPair settings, string relativePath, string localPath)
    {
        if (_transfer.Remove(localPath, settings.LocalPath, settings.VersionsKept, out var message))
        {
            _baseline.Remove(relativePath);
            _baselineStore.Remove(settings.Id, relativePath);
            _activity.Append(settings.Id, settings.Name, ActivityEventKind.Deleted, relativePath, ActivityResult.Ok);
        }
        else
        {
            _activity.Append(settings.Id, settings.Name, ActivityEventKind.Deleted, relativePath,
                ActivityResult.Failed, message: message);
        }
    }

    private void RaiseConflict(FolderPair settings, string relativePath, string localPath, string sharePath,
        SideInfo local, SideInfo share)
    {
        var (record, isNew) = _conflicts.Raise(
            settings.Id, settings.Name, settings.SharePath, relativePath,
            new ConflictSide
            {
                ModifiedUtc = DateTimeOffset.FromUnixTimeSeconds(local.ModifiedUnix),
                Bytes = local.Bytes,
                Owner = VolumeInfo.OwnerOf(localPath),
            },
            new ConflictSide
            {
                ModifiedUtc = DateTimeOffset.FromUnixTimeSeconds(share.ModifiedUnix),
                Bytes = share.Bytes,
                Owner = VolumeInfo.OwnerOf(sharePath),
            });

        _pendingConflicts = _conflicts.CountOpen(settings.Id);

        if (isNew)
        {
            _activity.Append(settings.Id, settings.Name, ActivityEventKind.Conflict, relativePath,
                ActivityResult.Pending);
            ConflictRaised?.Invoke(record, _pendingConflicts);
            _log.LogInformation("Pair {Name}: conflict on {Path}.", settings.Name, relativePath);
        }
    }

    private void StoreBaseline(string pairId, string relativePath, SideInfo local, SideInfo share)
    {
        var baseline = new FileBaseline
        {
            RelativePath = relativePath,
            LocalBytes = local.Exists ? local.Bytes : -1,
            LocalModified = local.Exists ? local.ModifiedUnix : 0,
            ShareBytes = share.Exists ? share.Bytes : -1,
            ShareModified = share.Exists ? share.ModifiedUnix : 0,
            SyncedUtc = DateTimeOffset.UtcNow,
        };

        _baseline[relativePath] = baseline;
        _baselineStore.Save(pairId, baseline);
    }

    // ---- Conflict resolution ------------------------------------------------

    private async Task RunCommandsAsync(CancellationToken ct)
    {
        if (_commands.Count == 0) return;

        var settings = CurrentSettings();
        var commands = _commands.ToList();
        _commands.Clear();

        foreach (var command in commands)
        {
            try
            {
                await ApplyResolutionAsync(settings, command.Conflict, command.Resolution, ct).ConfigureAwait(false);
                command.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pair {Name}: resolving {Path} failed.", settings.Name,
                    command.Conflict.RelativePath);
                command.Completion.TrySetException(ex);
            }
        }

        Settle();
    }

    private async Task ApplyResolutionAsync(FolderPair settings, ConflictRecord conflict,
        ConflictResolution resolution, CancellationToken ct)
    {
        var relativePath = conflict.RelativePath;
        var localPath = Path.Combine(settings.LocalPath, relativePath);
        var sharePath = Path.Combine(settings.SharePath, relativePath);

        switch (resolution)
        {
            case ConflictResolution.KeepLocal:
                await SendAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
                break;

            case ConflictResolution.KeepShare:
                await ReceiveAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
                break;

            case ConflictResolution.KeepBoth:
                await KeepBothAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
                break;
        }

        _conflicts.MarkResolved(conflict.Id, resolution);
        _pendingConflicts = _conflicts.CountOpen(settings.Id);
    }

    /// <summary>
    /// Renames this PC's copy to "&lt;name&gt; (&lt;user&gt;, &lt;machine&gt;).&lt;ext&gt;", then lets both files
    /// exist on both sides.
    /// </summary>
    private async Task KeepBothAsync(FolderPair settings, string relativePath, string localPath, string sharePath,
        CancellationToken ct)
    {
        var owner = VolumeInfo.OwnerOf(localPath);
        if (string.IsNullOrEmpty(owner)) owner = Environment.UserName;

        var renamed = StatusText.KeepBothName(Path.GetFileName(relativePath), owner, Environment.MachineName);
        var relativeDirectory = Path.GetDirectoryName(relativePath) ?? "";
        var renamedRelative = Path.Combine(relativeDirectory, renamed);
        var renamedLocal = Path.Combine(settings.LocalPath, renamedRelative);
        var renamedShare = Path.Combine(settings.SharePath, renamedRelative);

        if (!File.Exists(localPath))
            throw new FileNotFoundException("The local copy is gone; there is nothing to keep.", localPath);

        File.Move(localPath, renamedLocal, overwrite: true);
        _baseline.Remove(relativePath);
        _baselineStore.Remove(settings.Id, relativePath);

        // The renamed local copy goes up...
        await SendAsync(settings, renamedRelative, renamedLocal, renamedShare, ct).ConfigureAwait(false);

        // ...and the network copy comes down under the original name. Both transfers log their
        // own activity rows, so there is nothing more to record here.
        await ReceiveAsync(settings, relativePath, localPath, sharePath, ct).ConfigureAwait(false);
    }

    // ---- Enumeration & watchers ---------------------------------------------

    /// <summary>Adds every non-excluded relative path from both roots to <paramref name="into"/>.</summary>
    private void EnumerateInto(HashSet<string> into)
    {
        var settings = CurrentSettings();
        var before = into.Count;

        CollectRelative(settings.LocalPath, into);
        if (_reachable) CollectRelative(settings.SharePath, into);

        // Files that existed at the last sync but are gone from both roots still need a pass so
        // their tombstones are cleared and deletions propagate.
        foreach (var known in _baseline.Keys) into.Add(known);

        _log.LogDebug("Pair {Name}: scan queued {Count} path(s).", settings.Name, into.Count - before);
    }

    private void CollectRelative(string root, HashSet<string> into)
    {
        if (!Directory.Exists(root)) return;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(root, entry);
                if (_excludes.IsExcluded(relative)) continue;

                into.Add(relative);
                if (Directory.Exists(entry)) stack.Push(entry);
            }
        }
    }

    private void StartWatchers()
    {
        DisposeWatchers();

        var settings = CurrentSettings();
        if (!settings.Watcher) return;

        _localWatcher = TryWatch(settings.LocalPath, settings.LocalPath);

        // A pull- or two-way pair needs to see remote edits promptly. UNC watchers are not
        // guaranteed (older servers, some NAS firmware) — the periodic sweep covers the gap.
        if (settings.Direction != SyncDirection.Push)
            _shareWatcher = TryWatch(settings.SharePath, settings.SharePath);
    }

    private FileSystemWatcher? TryWatch(string root, string relativeTo)
    {
        try
        {
            if (!Directory.Exists(root)) return null;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            void Touch(string fullPath)
            {
                var relative = Path.GetRelativePath(relativeTo, fullPath);
                if (relative.StartsWith("..", StringComparison.Ordinal)) return;
                if (_excludes.IsExcluded(relative)) return;
                _queue.Enqueue(relative);
            }

            watcher.Created += (_, e) => Touch(e.FullPath);
            watcher.Changed += (_, e) => Touch(e.FullPath);
            watcher.Deleted += (_, e) => Touch(e.FullPath);
            watcher.Renamed += (_, e) => { Touch(e.OldFullPath); Touch(e.FullPath); };
            watcher.Error += (_, e) =>
            {
                // Overflowed buffer: individual events were dropped, so only a rescan is safe.
                _log.LogWarning(e.GetException(), "Watcher on {Root} errored; forcing a rescan.", root);
                RequestFullScan();
            };

            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not watch {Root}; falling back to the periodic sweep.", root);
            return null;
        }
    }

    private void DisposeWatchers()
    {
        _localWatcher?.Dispose();
        _shareWatcher?.Dispose();
        _localWatcher = null;
        _shareWatcher = null;
    }

    private void StartSweep()
    {
        _sweep?.Dispose();
        var interval = TimeSpan.FromSeconds(_config.SweepSeconds);
        _sweep = new Timer(_ => RequestFullScan(), null, interval, interval);
    }

    // ---- Small helpers ------------------------------------------------------

    private FolderPair CurrentSettings()
    {
        lock (_settingsGate) return _settings.CloneSettings();
    }

    private void UpdateGate()
    {
        var running = !_globalPause && CurrentSettings().Enabled;
        if (running) _resumeGate.Open();
        else _resumeGate.Close();
    }

    private PairStatus EffectiveStatus()
    {
        if (_globalPause || !_settings.Enabled) return PairStatus.Paused;
        if (!_reachable) return PairStatus.Waiting;
        if (!_settled && _status == PairStatus.InSync) return PairStatus.Syncing;
        return _status;
    }

    private string DescribeDetail(PairStatus status) => status switch
    {
        PairStatus.Paused => StatusText.Paused,
        PairStatus.Waiting => StatusText.Unreachable((int)Math.Ceiling((_retryAt - DateTimeOffset.UtcNow).TotalSeconds)),
        PairStatus.Conflict => StatusText.Conflicts(_pendingConflicts),
        PairStatus.Syncing when !_settled && _transientDetail is null => StatusText.StartingUp,
        PairStatus.Syncing => _transientDetail ?? StatusText.Sending("files", _batchDone, Math.Max(1, _batchTotal)),
        _ => _transientDetail ?? StatusText.UpToDate(_lastChecked),
    };

    /// <summary>Publishes the "Sending x — n of m files" line and wakes subscribers.</summary>
    private void AnnounceTransfer(string detail)
    {
        _status = PairStatus.Syncing;
        _transientDetail = detail;
        Changed?.Invoke(this);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a ?? ""),
            Path.TrimEndingDirectorySeparator(b ?? ""), StringComparison.OrdinalIgnoreCase);

    // ---- Work item plumbing -------------------------------------------------

    private enum WorkKind { Reconcile, FullScan, Resolve }

    private sealed record ResolveCommand(
        ConflictRecord Conflict,
        ConflictResolution Resolution,
        TaskCompletionSource<bool> Completion);

    private sealed record WorkItem(WorkKind Kind, string? RelativePath = null, ResolveCommand? Command = null)
    {
        public static readonly WorkItem Wake = new(WorkKind.FullScan);
        public static WorkItem Reconcile(string relativePath) => new(WorkKind.Reconcile, relativePath);
    }

    /// <summary>One side's on-disk facts, normalised to whole seconds for cross-filesystem comparison.</summary>
    private readonly struct SideInfo
    {
        private SideInfo(bool exists, long bytes, long modifiedUnix)
        {
            Exists = exists;
            Bytes = bytes;
            ModifiedUnix = modifiedUnix;
        }

        public bool Exists { get; }
        public long Bytes { get; }
        public long ModifiedUnix { get; }

        public static SideInfo Read(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new SideInfo(true, info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())
                    : new SideInfo(false, -1, 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SideInfo(false, -1, 0);
            }
        }

        public bool Matches(long baselineBytes, long baselineModified)
        {
            if (!Exists) return baselineBytes < 0;
            if (baselineBytes < 0) return false;
            return Bytes == baselineBytes && Math.Abs(ModifiedUnix - baselineModified) <= TimestampToleranceSeconds;
        }

        public bool SameContentAs(SideInfo other) =>
            Exists && other.Exists && Bytes == other.Bytes &&
            Math.Abs(ModifiedUnix - other.ModifiedUnix) <= TimestampToleranceSeconds;
    }
}

using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Tray.Services;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// Backs one folder pair's detail window: what this pair is doing, and everything you can do to it.
/// <para>
/// It owns no settings of its own. Editing opens the wizard in edit mode, removing opens the
/// confirmation from #15, and history opens the activity window filtered to this pair - so there
/// is one implementation of each rather than a second copy living here.
/// </para>
/// </summary>
public sealed class PairDetailViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly ShellViewModel _shell;

    private FolderPair _pair;
    private bool _busy;
    private string _error = "";
    private int _filesSent;
    private int _filesReceived;
    private int _conflicts;

    public PairDetailViewModel(ServiceConnection connection, ShellViewModel shell, FolderPair pair)
    {
        _connection = connection;
        _shell = shell;
        _pair = pair;

        SyncNowCommand = new RelayCommand(() => _ = SyncNowAsync(), () => !Busy);
        TogglePauseCommand = new RelayCommand(() => _ = TogglePauseAsync(), () => !Busy);

        // Follow the live row rather than the snapshot the window opened with: a pair that starts
        // syncing, finishes, or loses its share while this is open should say so.
        _shell.Pairs.CollectionChanged += (_, _) => Resync();
        foreach (var row in _shell.Pairs) Watch(row);

        _ = LoadSummaryAsync();
    }

    /// <summary>Raised for the surfaces this window hands off to, so the host opens them.</summary>
    public event Action<FolderPair>? EditRequested;
    public event Action<string>? ActivityRequested;
    public event Action<string>? RemoveRequested;
    public event Action? CloseRequested;

    public string Id => _pair.Id;

    public string Name => _pair.Name;

    public string LocalPath => _pair.LocalPath;

    public string SharePath => _pair.SharePath;

    /// <summary>The service's own words for what this pair is doing. Never re-derived here.</summary>
    public string Detail => _pair.Detail;

    public string StatusTag => _pair.Status switch
    {
        PairStatus.Syncing => "Syncing",
        PairStatus.InSync => "In sync",
        PairStatus.Conflict => "Conflict",
        PairStatus.Waiting => "Waiting",
        _ => "Paused",
    };

    public Icons.IconKey StatusIcon => _pair.Status switch
    {
        PairStatus.Syncing => Icons.IconKey.ArrowsClockwise,
        PairStatus.InSync => Icons.IconKey.Check,
        PairStatus.Conflict => Icons.IconKey.WarningDiamond,
        PairStatus.Waiting => Icons.IconKey.CloudSlash,
        _ => Icons.IconKey.Pause,
    };

    public string DirectionText => _pair.Direction switch
    {
        SyncDirection.Push => "One-way push — this PC wins, the share mirrors it",
        SyncDirection.Pull => "One-way pull — the share wins, this PC mirrors it",
        _ => "Two-way — newest change wins, conflicts are flagged",
    };

    public string WatcherText => _pair.Watcher
        ? "Realtime — changes copy as they happen"
        : "On the periodic sweep only";

    public string VersionsText => _pair.VersionsKept > 0
        ? $"Keeps {_pair.VersionsKept} previous version(s) of a changed file"
        : "No previous versions kept";

    public string LimitText => _pair.UploadLimitBytesPerSecond is { } limit and > 0
        ? $"Upload limited to {RateText.Format(limit)}"
        : "Upload unlimited";

    public string ExcludesText => _pair.Excludes.Count > 0
        ? string.Join(", ", _pair.Excludes)
        : "Nothing excluded";

    /// <summary>
    /// Per-pair pause, which is not the global one. A pair paused on its own stays paused when the
    /// user resumes everything, so the two must read differently.
    /// </summary>
    public bool IsPausedOnItsOwn => !_pair.Enabled;

    public string PauseLabel => IsPausedOnItsOwn ? "Resume this pair" : "Pause this pair";

    public Icons.IconKey PauseIcon => IsPausedOnItsOwn ? Icons.IconKey.Play : Icons.IconKey.Pause;

    /// <summary>Said out loud, because "Paused" on the row does not say by whom.</summary>
    public string PauseHint => IsPausedOnItsOwn
        ? "This pair is paused on its own. Resuming everything will not restart it."
        : "Stops just this pair. Other pairs keep syncing.";

    public string SummaryText =>
        $"{_filesSent:N0} sent · {_filesReceived:N0} received · {_conflicts:N0} conflicts";

    public bool HasConflicts => _pair.PendingConflicts > 0;

    public string ConflictText => _pair.PendingConflicts == 1
        ? "1 file needs you to choose which copy wins."
        : $"{_pair.PendingConflicts} files need you to choose which copy wins.";

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            SyncNowCommand.RaiseCanExecuteChanged();
            TogglePauseCommand.RaiseCanExecuteChanged();
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (!Set(ref _error, value)) return;
            Raise(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;

    public RelayCommand SyncNowCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public void RequestEdit() => EditRequested?.Invoke(_pair);

    public void RequestActivity() => ActivityRequested?.Invoke(_pair.Id);

    public void RequestRemove() => RemoveRequested?.Invoke(_pair.Id);

    private async Task SyncNowAsync()
    {
        Busy = true;
        try
        {
            var ack = await _connection.TryAsync(client => client.SyncNowAsync(_pair.Id))
                .ConfigureAwait(true);

            Error = ack is null ? "The DBsync service is not responding." : "";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task TogglePauseAsync()
    {
        Busy = true;
        try
        {
            var ack = await _connection.TryAsync(client =>
                client.SetPairEnabledAsync(_pair.Id, IsPausedOnItsOwn)).ConfigureAwait(true);

            Error = ack is null ? "The DBsync service is not responding." : "";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task LoadSummaryAsync()
    {
        var summary = await _connection.TryAsync(client => client.GetActivitySummaryAsync(
            new ActivityQuery { PairId = _pair.Id, Since = TimeSpan.FromDays(7) })).ConfigureAwait(true);

        if (summary is null) return;

        _filesSent = summary.FilesSent;
        _filesReceived = summary.FilesReceived;
        _conflicts = summary.Conflicts;
        Raise(nameof(SummaryText));
    }

    private void Watch(PairViewModel row)
    {
        if (row.Id != _pair.Id) return;
        row.PropertyChanged += (_, _) => Resync();
    }

    /// <summary>
    /// Pulls the live row's values back in. When the pair is gone - removed from another surface,
    /// or by the CLI - the window closes rather than showing detail for something that no longer
    /// exists.
    /// </summary>
    private void Resync()
    {
        var row = _shell.Pairs.FirstOrDefault(candidate => candidate.Id == _pair.Id);
        if (row is null)
        {
            CloseRequested?.Invoke();
            return;
        }

        _pair.Name = row.Name;
        _pair.LocalPath = row.LocalPath;
        _pair.SharePath = row.SharePath;
        _pair.Detail = row.Detail;
        _pair.Status = row.Status;
        _pair.NeedsCredentials = row.NeedsCredentials;
        _pair.Enabled = row.Enabled;

        RaiseAll();
    }

    /// <summary>
    /// Re-fetches the pair's own settings, then everything on screen. Called after an edit, where
    /// the row carries status but not the options that were just changed.
    /// </summary>
    public async Task ReloadAsync()
    {
        var state = await _connection.TryAsync(client => client.GetStateAsync()).ConfigureAwait(true);
        var fresh = state?.Pairs.FirstOrDefault(candidate => candidate.Id == _pair.Id);

        if (fresh is null)
        {
            CloseRequested?.Invoke();
            return;
        }

        _pair = fresh;
        RaiseAll();
        await LoadSummaryAsync().ConfigureAwait(true);
    }

    private void RaiseAll()
    {
        Raise(nameof(Name));
        Raise(nameof(LocalPath));
        Raise(nameof(SharePath));
        Raise(nameof(Detail));
        Raise(nameof(StatusTag));
        Raise(nameof(StatusIcon));
        Raise(nameof(DirectionText));
        Raise(nameof(WatcherText));
        Raise(nameof(VersionsText));
        Raise(nameof(LimitText));
        Raise(nameof(ExcludesText));
        Raise(nameof(IsPausedOnItsOwn));
        Raise(nameof(PauseLabel));
        Raise(nameof(PauseIcon));
        Raise(nameof(PauseHint));
        Raise(nameof(HasConflicts));
        Raise(nameof(ConflictText));
    }
}

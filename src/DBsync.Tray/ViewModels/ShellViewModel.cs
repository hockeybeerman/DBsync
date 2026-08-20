using System.Collections.ObjectModel;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Tray.Interop;
using DBsync.Tray.Notifications;
using DBsync.Tray.Services;
using DBsync.Tray.Theming;

namespace DBsync.Tray.ViewModels;

/// <summary>Everything the flyout binds to, plus the state the tray icon reads.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly ThemeManager _theme;
    private readonly System.Windows.Threading.Dispatcher _dispatcher =
        System.Windows.Application.Current.Dispatcher;

    private string _serviceStatusLine = "";
    private bool _anySyncing;
    private bool _pausedAll;
    private bool _isConnected;

    public ShellViewModel(ServiceConnection connection, ThemeManager theme)
    {
        _connection = connection;
        _theme = theme;

        _connection.StateReplaced += OnStateReplaced;
        _connection.PairChanged += OnPairChanged;
        _connection.ConnectionChanged += OnConnectionChanged;
        _connection.ConflictRaised += OnConflictRaised;
        _connection.ReachabilityChanged += OnReachabilityChanged;

        _theme.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ThemeManager.AppearanceSubline) or nameof(ThemeManager.Mode))
                RaiseAppearance();
        };

        TogglePauseCommand = new RelayCommand(TogglePause, () => IsConnected);
        StartServiceCommand = new RelayCommand(StartService, () => !Busy);
        RetryCommand = new RelayCommand(RetryConnection, () => !Busy);
        SetLightCommand = new RelayCommand(() => SetAppearance(AppearanceMode.Light));
        SetDarkCommand = new RelayCommand(() => SetAppearance(AppearanceMode.Dark));
        SetMatchWindowsCommand = new RelayCommand(() => SetAppearance(AppearanceMode.MatchWindows));
    }

    /// <summary>Raised when something the user did deserves a notification.</summary>
    public event Action<ToastMessage>? ToastRequested;

    public ObservableCollection<PairViewModel> Pairs { get; } = new();

    /// <summary>
    /// Header line under the title. The service's own wording — "Everything in sync", "2 files
    /// need your attention", "All syncing paused" — except when there is no service to ask.
    /// <para>
    /// Falling back to "Everything in sync" while disconnected would be the worst possible
    /// default: nothing is syncing, and the header would say so in the calmest words it has. The
    /// proper banner treatment is #12; this at least does not lie.
    /// </para>
    /// </summary>
    public string StatusLine => IsConnected
        ? _serviceStatusLine
        : "DBsync service is not running";

    /// <summary>Shown in place of the list. Distinguishes "no pairs" from "no service".</summary>
    public string EmptyStateText => IsConnected
        ? "No folder pairs yet. Add one to start syncing."
        : "Waiting for the DBsync service.";

    private void SetServiceStatusLine(string value)
    {
        if (_serviceStatusLine == value) return;
        _serviceStatusLine = value;
        Raise(nameof(StatusLine));
    }

    /// <summary>Drives the rotating header glyph and the tray icon animation.</summary>
    public bool AnySyncing
    {
        get => _anySyncing;
        private set => Set(ref _anySyncing, value);
    }

    public bool PausedAll
    {
        get => _pausedAll;
        private set
        {
            if (!Set(ref _pausedAll, value)) return;
            Raise(nameof(PauseButtonLabel));
            Raise(nameof(PauseButtonIcon));
            Raise(nameof(NeedsAttention));
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!Set(ref _isConnected, value)) return;
            Raise(nameof(HasPairs));
            Raise(nameof(StatusLine));
            Raise(nameof(EmptyStateText));
            RaiseBanner();
            TogglePauseCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasPairs => Pairs.Count > 0;

    // ---- Service-not-running banner -----------------------------------------

    /// <summary>
    /// The banner is shown whenever there is no service to talk to. It is not in the design, which
    /// never draws a disconnected flyout, but the alternative is a pair list that looks live while
    /// nothing is syncing.
    /// </summary>
    public bool ShowBanner => !IsConnected;

    /// <summary>
    /// True when rows are being shown from a connection that has since dropped. They are the last
    /// thing known to be true, which is worth keeping — but they must not read as current.
    /// </summary>
    public bool PairsAreStale => !IsConnected && Pairs.Count > 0;

    public string BannerTitle => Busy ? "Starting the DBsync service" : "Syncing is stopped";

    /// <summary>
    /// Says which of the two situations this is. "Not installed" and "installed but stopped" need
    /// different things from the user, so they are never collapsed into one message.
    /// </summary>
    public string BannerBody => _serviceState switch
    {
        _ when Busy => "Waiting for it to come up.",

        // "Not installed" is only credible if we never reached it. Having talked to it and then
        // lost it means something is there — a manually run instance, or one just uninstalled —
        // and telling the user to install it would send them the wrong way.
        ServiceInstallState.NotInstalled when _connection.HasEverConnected =>
            "The DBsync service has stopped, so nothing is syncing.",
        ServiceInstallState.NotInstalled =>
            "The DBsync service is not installed on this PC, so nothing can sync.",

        ServiceInstallState.Changing => "The service is starting or stopping. This should clear itself.",
        ServiceInstallState.Running =>
            "The service is running but not reachable. Retrying automatically.",
        _ => "The DBsync service is not running, so nothing is syncing.",
    };

    /// <summary>Offered only when there is actually a stopped service to start.</summary>
    public bool CanStartService =>
        _serviceState is ServiceInstallState.Stopped or ServiceInstallState.Unknown;

    public RelayCommand StartServiceCommand { get; }
    public RelayCommand RetryCommand { get; }

    private ServiceInstallState _serviceState = ServiceInstallState.Unknown;
    private bool _busy;

    private bool Busy
    {
        get => _busy;
        set
        {
            if (!Set(ref _busy, value)) return;
            RaiseBanner();
            StartServiceCommand.RaiseCanExecuteChanged();
            RetryCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseBanner()
    {
        Raise(nameof(ShowBanner));
        Raise(nameof(PairsAreStale));
        Raise(nameof(BannerTitle));
        Raise(nameof(BannerBody));
        Raise(nameof(CanStartService));
    }

    /// <summary>
    /// Asks the SCM what it thinks, so the banner can say something specific. Cheap, and only run
    /// while disconnected.
    /// </summary>
    private void RefreshServiceState()
    {
        _serviceState = ServiceControl.Query();
        RaiseBanner();
    }

    private void StartService()
    {
        Busy = true;

        _ = Task.Run(() =>
        {
            var started = ServiceControl.TryStartElevated(out var error);

            _dispatcher.BeginInvoke(() =>
            {
                Busy = false;
                RefreshServiceState();

                // Whether or not sc.exe reported success, go and look: the reconnect is what
                // actually decides, and it is now rather than up to 30 seconds away.
                if (started) _connection.RetryNow();
                else if (error is { Length: > 0 }) ToastRequested?.Invoke(
                    new ToastMessage(ToastKind.Paused, "Could not start the service", error));
            });
        });
    }

    private void RetryConnection()
    {
        RefreshServiceState();
        _connection.RetryNow();
    }

    /// <summary>Colours the header line accent rather than calm. Mirrors the service's own count.</summary>
    public bool NeedsAttention => !PausedAll && Pairs.Any(pair => pair.Status == PairStatus.Conflict);

    public string PauseButtonLabel => PausedAll ? "Resume all" : "Pause all";

    public Icons.IconKey PauseButtonIcon => PausedAll ? Icons.IconKey.Play : Icons.IconKey.Pause;

    // ---- Appearance ---------------------------------------------------------

    public string AppearanceSubline => _theme.AppearanceSubline;

    public bool IsLightSelected => _theme.Mode == AppearanceMode.Light;

    public bool IsDarkSelected => _theme.Mode == AppearanceMode.Dark;

    public bool IsMatchWindowsSelected => _theme.Mode == AppearanceMode.MatchWindows;

    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand SetLightCommand { get; }
    public RelayCommand SetDarkCommand { get; }
    public RelayCommand SetMatchWindowsCommand { get; }

    private void SetAppearance(AppearanceMode mode) => _theme.Mode = mode;

    private void RaiseAppearance()
    {
        Raise(nameof(AppearanceSubline));
        Raise(nameof(IsLightSelected));
        Raise(nameof(IsDarkSelected));
        Raise(nameof(IsMatchWindowsSelected));
    }

    // ---- Service state ------------------------------------------------------

    private void OnStateReplaced(ServiceState state)
    {
        SetServiceStatusLine(state.StatusLine);
        AnySyncing = state.AnySyncing;
        PausedAll = state.PausedAll;

        // Reconcile in place rather than clearing: a rebuilt collection would restart the
        // progress sweep animation and drop selection on every push.
        var incoming = state.Pairs;

        for (var i = Pairs.Count - 1; i >= 0; i--)
        {
            if (incoming.All(pair => pair.Id != Pairs[i].Id)) Pairs.RemoveAt(i);
        }

        for (var i = 0; i < incoming.Count; i++)
        {
            var existing = Pairs.FirstOrDefault(row => row.Id == incoming[i].Id);
            if (existing is null)
            {
                Pairs.Insert(Math.Min(i, Pairs.Count), new PairViewModel(incoming[i]));
                continue;
            }

            existing.Apply(incoming[i]);

            var currentIndex = Pairs.IndexOf(existing);
            if (currentIndex != i) Pairs.Move(currentIndex, i);
        }

        Raise(nameof(HasPairs));
        Raise(nameof(NeedsAttention));
        Raise(nameof(PairsAreStale));
    }

    private void OnPairChanged(PairChangedEvent changed)
    {
        SetServiceStatusLine(changed.StatusLine);
        AnySyncing = changed.AnySyncing;

        var row = Pairs.FirstOrDefault(pair => pair.Id == changed.Pair.Id);
        if (row is not null)
        {
            row.Apply(changed.Pair);
            Raise(nameof(NeedsAttention));
            return;
        }

        // A pair we have never seen: ask for the authoritative list rather than inventing one
        // from a single event.
        _ = _connection.TryAsync(async client =>
        {
            var state = await client.GetStateAsync().ConfigureAwait(false);
            await _dispatcher.BeginInvoke(() => OnStateReplaced(state));
            return true;
        });
    }

    /// <summary>
    /// A conflict stops a pair syncing and needs a person, so it is worth interrupting for — even
    /// though the design's table does not list a toast for it.
    /// </summary>
    private void OnConflictRaised(ConflictRaisedEvent raised)
    {
        var file = System.IO.Path.GetFileName(raised.Conflict.RelativePath);
        ToastRequested?.Invoke(ToastCopy.ConflictRaised(file, raised.Conflict.PairName));
    }

    /// <summary>
    /// A refused sign-in is worth interrupting for: unlike an outage it will never clear on its
    /// own, and the flyout is usually closed when credentials expire.
    /// </summary>
    private void OnReachabilityChanged(ReachabilityEvent reachability)
    {
        if (!reachability.NeedsCredentials) return;

        var name = reachability.PairName is { Length: > 0 } named ? named : "A folder pair";
        ToastRequested?.Invoke(ToastCopy.CredentialsNeeded(name));
    }

    private void OnConnectionChanged(bool connected)
    {
        IsConnected = connected;

        if (connected) return;

        // Only worth asking the SCM when there is something to explain.
        RefreshServiceState();

        // Nothing is moving while the service is away; a spinning glyph would claim otherwise.
        AnySyncing = false;
    }

    private void TogglePause()
    {
        var pausing = !PausedAll;

        // Optimistic, then corrected by the StateChanged event the service broadcasts. Waiting
        // for the round trip makes the button feel dead on a busy service.
        PausedAll = pausing;

        _ = _connection.TryAsync(async client =>
        {
            var state = pausing
                ? await client.PauseAllAsync().ConfigureAwait(false)
                : await client.ResumeAllAsync().ConfigureAwait(false);

            // The resume copy names how much is queued, which only the service knows — so the
            // toast waits for the response rather than guessing alongside the optimistic flip.
            await _dispatcher.BeginInvoke(() => ToastRequested?.Invoke(
                pausing ? ToastCopy.Paused() : ToastCopy.Resumed(state.QueuedChanges)));

            return true;
        });
    }
}

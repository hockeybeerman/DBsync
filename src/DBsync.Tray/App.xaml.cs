using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DBsync.Tray.Dev;
using DBsync.Tray.Notifications;
using DBsync.Tray.Services;
using DBsync.Tray.Theming;
using DBsync.Tray.Tray;
using DBsync.Tray.ViewModels;
using DBsync.Tray.Views;

namespace DBsync.Tray;

public partial class App : Application
{
    private ThemeManager? _theme;
    private ServiceConnection? _connection;
    private ShellViewModel? _shell;
    private TrayIconHost? _tray;
    private FlyoutWindow? _flyout;
    private WizardWindow? _wizard;
    private ActivityWindow? _activity;
    private ConflictDialog? _conflict;
    private ToastService? _toasts;
    private ToastStack? _toastStack;
    private CredentialsDialog? _credentials;
    private SingleInstance? _instance;
    private RemovePairDialog? _removal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything else: an unhandled exception in a tray app otherwise removes the icon
        // and leaves no trace of why.
        CrashLog.Install(this);

        // Theming comes up before any window, so the first frame is already in the right palette
        // rather than flashing the default one.
        _theme = ThemeManager.Initialize(this);

        if (e.Args.Contains("--audit", StringComparer.OrdinalIgnoreCase))
        {
            RunAudit();
            return;
        }

        if (e.Args.Contains("--gallery", StringComparer.OrdinalIgnoreCase))
        {
            MainWindow = new ThemeGalleryWindow();
            MainWindow.Closed += (_, _) => Shutdown();
            MainWindow.Show();
            return;
        }

        // Only one tray per session. --audit and --gallery are exempt above: they put no icon in
        // the tray, and blocking them would make a build impossible to inspect while the real app
        // is running.
        _instance = SingleInstance.Claim();
        if (_instance is null)
        {
            // The running instance is showing itself instead. Leaving quietly is the whole point.
            Shutdown();
            return;
        }

        StartTrayApp(
            showImmediately: e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase),
            pinOpen: e.Args.Contains("--pin", StringComparer.OrdinalIgnoreCase));

        // Signalled from a background thread, so hop to the UI thread before touching a window.
        _instance.ShowRequested += () => Dispatcher.BeginInvoke(new Action(() => _flyout?.ShowFlyout()));
        _instance.ListenForOtherLaunches();

        if (e.Args.Contains("--wizard", StringComparer.OrdinalIgnoreCase)) ShowWizard();
    }

    /// <param name="showImmediately">
    /// Opens the flyout at launch instead of waiting for a tray click. For development and
    /// screenshots — the shipped behaviour is tray-icon-only.
    /// </param>
    /// <param name="pinOpen">
    /// Keeps the flyout open when it loses focus. Development only, so the panel can be inspected
    /// while something else drives the service.
    /// </param>
    private void StartTrayApp(bool showImmediately, bool pinOpen)
    {
        _connection = new ServiceConnection();
        _shell = new ShellViewModel(_connection, _theme!);

        _flyout = new FlyoutWindow(_shell) { StayOpenOnDeactivate = pinOpen };
        _flyout.PairActivated += OnPairActivated;
        _flyout.NavigationRequested += OnNavigationRequested;
        _flyout.PairRemoveRequested += ShowRemovePair;

        // Toasts before the tray icon, so a notification raised during the first state push has
        // somewhere to go.
        _toastStack = new ToastStack(() => _flyout is { IsVisible: true } ? _flyout.ActualHeight : 0);
        _toastStack.Clicked += message => OnToastActivated(message.Kind);

        _toasts = new ToastService(message => _toastStack.Show(message));
        _toasts.Activated += OnToastActivated;

        _shell.ToastRequested += message => _toasts.Show(message);

        _tray = new TrayIconHost(_shell, _theme!.Windows);
        _tray.ToggleRequested += () => _flyout.ToggleFlyout();
        _tray.OpenRequested += () => _flyout.ShowFlyout();
        _tray.PauseRequested += () => _shell.TogglePauseCommand.Execute(null);
        _tray.QuitRequested += Quit;

        // The tray icon is the app's presence; no window is shown until the user asks for one.
        _connection.Start();

        if (showImmediately) _flyout.ShowFlyout();
    }

    /// <summary>
    /// Row click: Conflict opens the conflict dialog, anything else that pair's detail. Neither
    /// surface exists yet (#5, #9), so this is where they will be wired in.
    /// </summary>
    private void OnPairActivated(PairViewModel pair)
    {
        if (pair.Status == Contracts.PairStatus.Conflict)
        {
            ShowConflicts(pair.Id);
            return;
        }

        // A refused sign-in is the one thing the user can fix from the row itself.
        if (pair.NeedsCredentials)
        {
            ShowCredentials(pair.Id);
            return;
        }

        // The prototype opens the activity window for a non-conflict row as scaffolding; the real
        // target is that pair's detail view, which is #9. Until then, show its history filtered to
        // it, which is at least about the row that was clicked.
        ShowActivity(pair.Id);
    }

    private void OnNavigationRequested(string target)
    {
        switch (target)
        {
            case "wizard":
                ShowWizard();
                break;
            case "activity":
                ShowActivity();
                break;
            default:
                ReportUnbuilt("A settings window is not designed yet — see issue #8.");
                break;
        }
    }

    /// <summary>
    /// A clicked toast opens whatever it was about. Anything with no obvious destination brings
    /// the flyout up, which is the app's front door.
    /// </summary>
    private void OnToastActivated(ToastKind kind)
    {
        switch (kind)
        {
            case ToastKind.ConflictRaised:
            case ToastKind.ConflictResolved:
            case ToastKind.BothCopiesKept:
                ShowConflicts();
                break;
            case ToastKind.CredentialsNeeded:
                ShowCredentials();
                break;
            case ToastKind.PairCreated:
            case ToastKind.Paused:
            case ToastKind.Resumed:
            default:
                _flyout?.ShowFlyout();
                break;
        }
    }

    /// <summary>
    /// Opens the sign-in re-prompt for a pair whose credentials were refused. With no id, picks
    /// the first pair asking for one — which is what a toast click means.
    /// </summary>
    private void ShowCredentials(string? pairId = null)
    {
        if (_credentials is not null)
        {
            _credentials.Activate();
            return;
        }

        _ = ShowCredentialsAsync(pairId);
    }

    private async Task ShowCredentialsAsync(string? pairId)
    {
        // Fetch the pair fresh rather than using the row's projection: the dialog needs the share
        // path and name as the service currently has them.
        var state = await _connection!.TryAsync(client => client.GetStateAsync());
        var pair = pairId is null
            ? state?.Pairs.FirstOrDefault(candidate => candidate.NeedsCredentials)
            : state?.Pairs.FirstOrDefault(candidate => candidate.Id == pairId);

        if (pair is null)
        {
            ReportUnbuilt("That folder pair is no longer asking for a sign-in.");
            return;
        }

        _flyout?.HideFlyout();

        var viewModel = new CredentialsViewModel(_connection!, pair);
        _credentials = new CredentialsDialog(viewModel);
        _credentials.Completed += toast => { if (toast is not null) _toasts?.Show(toast); };
        _credentials.Closed += (_, _) => _credentials = null;
        _credentials.Show();
        _credentials.Activate();
    }

    /// <summary>
    /// Confirms removing a folder pair. The row's own projection is enough here - unlike the
    /// sign-in dialog, nothing needs re-fetching, and the paths shown are the ones the user was
    /// just looking at.
    /// </summary>
    private void ShowRemovePair(PairViewModel pair)
    {
        if (_removal is not null)
        {
            _removal.Activate();
            return;
        }

        _flyout?.HideFlyout();

        var viewModel = new RemovePairViewModel(_connection!, pair);
        _removal = new RemovePairDialog(viewModel);
        _removal.Completed += toast => { if (toast is not null) _toasts?.Show(toast); };
        _removal.Closed += (_, _) => _removal = null;
        _removal.Show();
        _removal.Activate();
    }

    /// <summary>
    /// Opens the conflict dialog on the open conflicts, optionally scoped to one pair. Closes
    /// itself once there is nothing left to decide.
    /// </summary>
    private void ShowConflicts(string? pairId = null, Window? owner = null)
    {
        if (_conflict is not null)
        {
            _conflict.Activate();
            return;
        }

        if (owner is null) _flyout?.HideFlyout();

        var viewModel = new ConflictViewModel(_connection!);
        _conflict = new ConflictDialog(viewModel, owner);
        _conflict.Resolved += message => _toasts?.Show(message);
        _conflict.Closed += (_, _) => _conflict = null;
        _conflict.ShowWithBackdrop();

        _ = viewModel.LoadAsync(pairId);
    }

    /// <summary>
    /// Opens the activity window, optionally scoped to one pair. Only one is kept, so repeated
    /// clicks bring the existing window forward rather than stacking copies.
    /// </summary>
    private void ShowActivity(string? pairId = null)
    {
        _flyout?.HideFlyout();

        if (_activity is not null)
        {
            if (_activity.WindowState == WindowState.Minimized) _activity.WindowState = WindowState.Normal;
            _activity.Activate();
            return;
        }

        var viewModel = new ActivityViewModel(_connection!);
        _activity = new ActivityWindow(viewModel);
        _activity.ExportRequested += () => ReportUnbuilt("CSV export arrives with issue #7.");
        _activity.ReviewConflictsRequested += () => ShowConflicts(owner: _activity);
        _activity.Closed += (_, _) => _activity = null;
        _activity.Show();
        _activity.Activate();

        if (pairId is not null) viewModel.SelectPair(pairId);
    }

    /// <summary>
    /// Opens the add/edit wizard. <paramref name="editing"/> is null for a new pair; #9 will pass
    /// an existing one.
    /// </summary>
    private void ShowWizard(Contracts.FolderPair? editing = null)
    {
        if (_wizard is not null)
        {
            _wizard.Activate();
            return;
        }

        // The flyout dismisses on deactivate, so it is already gone by the time the wizard has
        // focus — which is what keeps the two from overlapping.
        _flyout?.HideFlyout();

        var viewModel = new WizardViewModel(_connection!, UserSettings.Current,
            _shell?.ServiceAccount ?? "", editing);
        _wizard = new WizardWindow(viewModel);
        _wizard.Completed += saved =>
        {
            _wizard = null;
            if (saved is not null && editing is null)
                _toasts?.Show(ToastCopy.PairCreated(saved.LocalPath, saved.SharePath));
        };
        _wizard.Closed += (_, _) =>
        {
            viewModel.Detach();
            _wizard = null;
        };
        _wizard.Show();
        _wizard.Activate();
    }

    /// <summary>
    /// Says plainly that a surface is not built rather than pretending the click did something.
    /// Every one of these disappears as its issue lands.
    /// </summary>
    private void ReportUnbuilt(string message) =>
        MessageBox.Show(message, "DBsync", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Quit()
    {
        if (_flyout is not null)
        {
            _flyout.AllowClose = true;
            _flyout.Close();
        }

        Shutdown();
    }

    /// <summary>
    /// Headless check that both palettes match the design's table and that Match Windows follows
    /// the OS. Exits 0 when everything matches, 1 otherwise, so it can gate a build.
    /// </summary>
    private void RunAudit()
    {
        var (report, passed) = TokenAudit.RunBothVariants(_theme!);
        var summary = passed
            ? "Theme audit passed — every role matches docs/README.md §Theming."
            : "Theme audit FAILED — see the rows marked FAIL above.";
        var output = report + summary + Environment.NewLine;

        // A WinExe has no console of its own; borrow the launching one when there is one, and
        // always leave the report on disk so a detached run is still inspectable.
        var logPath = Path.Combine(Path.GetTempPath(), "dbsync-theme-audit.txt");
        try { File.WriteAllText(logPath, output); } catch (IOException) { /* report still goes to stdout */ }

        if (AttachConsole(AttachParentProcess))
        {
            Console.Out.Write(output);
            Console.Out.Write($"(also written to {logPath}){Environment.NewLine}");
            Console.Out.Flush();
        }

        Shutdown(passed ? 0 : 1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _toastStack?.CloseAll();
        _toasts?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        _connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _theme?.Dispose();
        base.OnExit(e);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}

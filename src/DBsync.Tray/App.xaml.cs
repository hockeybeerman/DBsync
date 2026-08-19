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

        StartTrayApp(
            showImmediately: e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase),
            pinOpen: e.Args.Contains("--pin", StringComparer.OrdinalIgnoreCase));

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
            case ToastKind.PairCreated:
            case ToastKind.Paused:
            case ToastKind.Resumed:
            default:
                _flyout?.ShowFlyout();
                break;
        }
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

        var viewModel = new WizardViewModel(_connection!, UserSettings.Current, editing);
        _wizard = new WizardWindow(viewModel);
        _wizard.Completed += saved =>
        {
            _wizard = null;
            if (saved is not null && editing is null)
                _toasts?.Show(ToastCopy.PairCreated(saved.LocalPath, saved.SharePath));
        };
        _wizard.Closed += (_, _) => _wizard = null;
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
        _connection?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _theme?.Dispose();
        base.OnExit(e);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}

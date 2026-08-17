using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DBsync.Tray.Dev;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
    private void OnPairActivated(PairViewModel pair) =>
        ReportUnbuilt(pair.Status == Contracts.PairStatus.Conflict
            ? "The conflict dialog arrives with issue #5."
            : "Per-pair settings arrive with issue #9.");

    private void OnNavigationRequested(string target) => ReportUnbuilt(target switch
    {
        "wizard" => "The add/edit pair wizard arrives with issue #3.",
        "activity" => "The activity window arrives with issue #4.",
        _ => "A settings window is not designed yet — see issue #8.",
    });

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

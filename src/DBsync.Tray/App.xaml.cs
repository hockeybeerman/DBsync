using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DBsync.Tray.Dev;
using DBsync.Tray.Theming;

namespace DBsync.Tray;

public partial class App : Application
{
    private ThemeManager? _theme;

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

        // Until the flyout lands (#2), the gallery is the app's only window and doubles as the
        // verification harness for this theming pass.
        MainWindow = new ThemeGalleryWindow();
        MainWindow.Show();
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
        _theme?.Dispose();
        base.OnExit(e);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}

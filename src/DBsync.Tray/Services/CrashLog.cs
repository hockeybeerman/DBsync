using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using DBsync.Tray.Theming;

namespace DBsync.Tray.Services;

/// <summary>
/// Last line of defence for unhandled exceptions.
/// <para>
/// A tray app has no window most of the time, so an unhandled exception takes the process down
/// with nothing on screen: the icon simply disappears and syncing appears to have stopped, when
/// in fact the service is still running perfectly well. This records what happened and says so,
/// rather than vanishing.
/// </para>
/// </summary>
public static class CrashLog
{
    public static string FilePath => Path.Combine(UserSettings.Directory, "tray-errors.log");

    public static void Install(Application application)
    {
        application.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Write("UI thread", e.Exception);

        // Keep running. A failure painting one window is not a reason to take the tray icon away
        // and leave the user with no way back in.
        e.Handled = true;

        MessageBox.Show(
            $"Something went wrong in the DBsync tray app.\n\n{e.Exception.Message}\n\n" +
            $"Details were written to {FilePath}",
            "DBsync", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e) =>
        Write("background thread", e.ExceptionObject as Exception);

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Write("unobserved task", e.Exception);
        e.SetObserved();
    }

    private static void Write(string origin, Exception? exception)
    {
        if (exception is null) return;

        try
        {
            Directory.CreateDirectory(UserSettings.Directory);

            var entry = new StringBuilder()
                .AppendLine($"--- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ({origin}) ---")
                .AppendLine(exception.ToString())
                .AppendLine();

            File.AppendAllText(FilePath, entry.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write to. Losing the log is not worth a second failure.
        }
    }
}

using System.IO;
using DBsync.Tray.Theming;

namespace DBsync.Tray.Services;

/// <summary>
/// A short breadcrumb trail for behaviour that cannot be observed from inside the app.
/// <para>
/// Toast activation is the case that motivated it: the click happens in the shell, is routed
/// through a COM server, and may never reach this process at all — so when nothing happens there
/// is otherwise no way to tell "the shell never called us" from "we were called and ignored it".
/// </para>
/// </summary>
public static class Diagnostics
{
    public static string FilePath => Path.Combine(UserSettings.Directory, "tray-diagnostics.log");

    public static void Note(string message)
    {
        try
        {
            Directory.CreateDirectory(UserSettings.Directory);
            File.AppendAllText(FilePath,
                $"{DateTimeOffset.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never be the reason something fails.
        }
    }
}

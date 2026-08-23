using Microsoft.Win32;

namespace DBsync.Tray.Interop;

/// <summary>
/// Whether the tray app starts when this user signs in.
/// <para>
/// Deliberately per-user, under HKCU. The service is machine-wide and starts on its own; the tray
/// is one person's window onto it, and a machine-wide Run entry cannot be turned off by the person
/// it launches for without administrator rights - which would make the setting a lie.
/// </para>
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DBsync";

    /// <summary>
    /// True when this user's Run key points at an executable. The path is not compared: an entry
    /// left by an older install still means "start at sign-in", and rewriting it is this class's
    /// job rather than a reason to report the setting as off.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Points an existing entry at the running executable. Without this an upgrade that moves the
    /// exe - or a run from a build output - leaves the Run key aimed at a path that may no longer
    /// exist, and sign-in launch quietly stops working with the setting still showing as on.
    /// </summary>
    public static void RefreshPath()
    {
        if (!IsEnabled()) return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;

            var expected = $"\"{exe}\"";
            if (key.GetValue(ValueName) is string current
                && !string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, expected, RegistryValueKind.String);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
            or System.IO.IOException)
        {
        }
    }

    /// <summary>
    /// Turns sign-in launch on or off. Returns false when the registry refused, so the caller can
    /// say the setting did not take rather than showing a checkbox that quietly disagrees with the
    /// machine.
    /// </summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;

                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
            or System.IO.IOException)
        {
            return false;
        }
    }
}

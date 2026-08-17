using System.IO;
using Microsoft.Win32;

namespace DBsync.Tray.Theming;

/// <summary>
/// Reads the Windows app theme and reports when the user changes it.
/// <para>
/// <c>AppsUseLightTheme</c> is the app-level preference, deliberately not
/// <c>SystemUsesLightTheme</c> — the latter drives the taskbar and Start menu, and the two can
/// legitimately differ. The tray flyout is an app surface, so it follows the app value.
/// </para>
/// </summary>
public sealed class WindowsTheme : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";
    private const string SystemUsesLightTheme = "SystemUsesLightTheme";

    private bool _subscribed;
    private bool _disposed;

    public WindowsTheme()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _subscribed = true;
    }

    /// <summary>Raised when the Windows app theme changes. Fires on an arbitrary thread.</summary>
    public event EventHandler<ThemeVariant>? Changed;

    /// <summary>Raised when the shell (taskbar) theme changes. Fires on an arbitrary thread.</summary>
    public event EventHandler<ThemeVariant>? ShellChanged;

    /// <summary>
    /// The current Windows app theme. Defaults to <see cref="ThemeVariant.Dark"/> when the value
    /// is missing or unreadable.
    /// </summary>
    public static ThemeVariant Current => Read(AppsUseLightTheme);

    /// <summary>
    /// The theme the shell itself uses — taskbar, Start, system tray. Distinct from
    /// <see cref="Current"/>: Windows lets the two differ, and the tray glyph sits on the
    /// taskbar's ground, not the app's, so it has to follow this one to stay visible.
    /// </summary>
    public static ThemeVariant Shell => Read(SystemUsesLightTheme);

    private static ThemeVariant Read(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(valueName) is int value && value != 0
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                       or IOException)
        {
            // Nocturne's native ground, so a locked-down machine still gets the palette the
            // system was designed in.
            return ThemeVariant.Dark;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // A theme switch arrives as a General change, along with a good deal else. Re-reading the
        // registry keys is cheap, and each listener only acts when its value actually moved.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)) return;

        Changed?.Invoke(this, Current);
        ShellChanged?.Invoke(this, Shell);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_subscribed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _subscribed = false;
        }
    }
}

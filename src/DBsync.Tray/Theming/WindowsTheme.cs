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

    private bool _subscribed;
    private bool _disposed;

    public WindowsTheme()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _subscribed = true;
    }

    /// <summary>Raised when the Windows app theme changes. Fires on an arbitrary thread.</summary>
    public event EventHandler<ThemeVariant>? Changed;

    /// <summary>
    /// The current Windows app theme. Defaults to <see cref="ThemeVariant.Dark"/> when the value
    /// is missing or unreadable — Nocturne's native ground, so a locked-down or unusual machine
    /// still gets the palette the system was designed in.
    /// </summary>
    public static ThemeVariant Current
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue(AppsUseLightTheme) is int value && value != 0
                    ? ThemeVariant.Light
                    : ThemeVariant.Dark;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                           or IOException)
            {
                return ThemeVariant.Dark;
            }
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // A theme switch arrives as a General change, along with a good deal else. Re-reading the
        // registry key is cheap, and the manager only re-themes when the value actually moved.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            Changed?.Invoke(this, Current);
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

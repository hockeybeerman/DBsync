namespace DBsync.Tray.Theming;

/// <summary>
/// The three options on the flyout's Appearance row. Persisted per user; the default is
/// <see cref="MatchWindows"/>.
/// </summary>
public enum AppearanceMode
{
    /// <summary>Follow the Windows app theme, live.</summary>
    MatchWindows,

    Light,

    Dark,
}

/// <summary>Which palette is actually in force once <see cref="AppearanceMode"/> is resolved.</summary>
public enum ThemeVariant
{
    Light,
    Dark,
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace DBsync.Tray.Theming;

/// <summary>
/// Owns the app's appearance: resolves <see cref="AppearanceMode"/> against the OS, swaps the
/// role-token dictionary, and keeps following Windows while in Match Windows mode.
/// <para>
/// Only one dictionary is ever exchanged — <c>Theme.Dark.xaml</c> for <c>Theme.Light.xaml</c>.
/// Ramps, metrics, typography and control styles are mode-independent and stay put, so a swap
/// is one dictionary write and every <c>DynamicResource</c> in the tree repaints itself.
/// </para>
/// </summary>
public sealed class ThemeManager : INotifyPropertyChanged, IDisposable
{
    private const string DarkDictionary = "Themes/Theme.Dark.xaml";
    private const string LightDictionary = "Themes/Theme.Light.xaml";

    private readonly Application _application;
    private readonly UserSettings _settings;
    private readonly WindowsTheme _windows;

    private ResourceDictionary? _active;
    private AppearanceMode _mode;
    private ThemeVariant _resolved;
    private bool _disposed;

    private ThemeManager(Application application, UserSettings settings)
    {
        _application = application;
        _settings = settings;
        _mode = settings.Appearance;

        _windows = new WindowsTheme();
        _windows.Changed += OnWindowsThemeChanged;

        Apply(Resolve(_mode), force: true);
    }

    /// <summary>The process-wide instance. Set up once by <c>App</c> at startup.</summary>
    public static ThemeManager Current { get; private set; } = null!;

    /// <summary>
    /// The OS theme watcher. Exposed so the tray icon can follow the *shell* theme, which is a
    /// different preference from the app theme this manager tracks.
    /// </summary>
    public WindowsTheme Windows => _windows;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the palette actually changes, with the new variant.</summary>
    public event EventHandler<ThemeVariant>? ThemeChanged;

    public static ThemeManager Initialize(Application application)
    {
        Current = new ThemeManager(application, UserSettings.Current);
        return Current;
    }

    /// <summary>The user's choice. Setting it applies and persists immediately.</summary>
    public AppearanceMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;

            _mode = value;
            _settings.Appearance = value;
            _settings.Save();

            Apply(Resolve(value), force: false);
            Notify();
            Notify(nameof(AppearanceSubline));
        }
    }

    /// <summary>Which palette is in force right now.</summary>
    public ThemeVariant Resolved
    {
        get => _resolved;
        private set
        {
            if (_resolved == value) return;
            _resolved = value;
            Notify();
        }
    }

    /// <summary>
    /// The live subline under the flyout's "Appearance" label. The design specifies all four
    /// strings, and the Match Windows ones name the resolved variant so the row explains itself.
    /// </summary>
    public string AppearanceSubline => _mode switch
    {
        AppearanceMode.Light => "Always light",
        AppearanceMode.Dark => "Always dark",
        _ => Resolved == ThemeVariant.Light ? "Matching Windows — light" : "Matching Windows — dark",
    };

    private static ThemeVariant Resolve(AppearanceMode mode) => mode switch
    {
        AppearanceMode.Light => ThemeVariant.Light,
        AppearanceMode.Dark => ThemeVariant.Dark,
        _ => WindowsTheme.Current,
    };

    private void OnWindowsThemeChanged(object? sender, ThemeVariant variant)
    {
        // SystemEvents fires on its own thread; resource dictionaries belong to the UI thread.
        _application.Dispatcher.BeginInvoke(() =>
        {
            if (_mode != AppearanceMode.MatchWindows) return;

            Apply(variant, force: false);
            Notify(nameof(AppearanceSubline));
        });
    }

    private void Apply(ThemeVariant variant, bool force)
    {
        if (!force && variant == _resolved && _active is not null) return;

        var source = new Uri(variant == ThemeVariant.Light ? LightDictionary : DarkDictionary,
            UriKind.Relative);
        var incoming = new ResourceDictionary { Source = source };

        var merged = _application.Resources.MergedDictionaries;

        // Swap in place so the role tokens keep their position in the merge order — appended at
        // the end they would win over anything a window merges locally.
        if (_active is not null && merged.Contains(_active))
        {
            merged[merged.IndexOf(_active)] = incoming;
        }
        else
        {
            merged.Add(incoming);
        }

        _active = incoming;
        Resolved = variant;
        ThemeChanged?.Invoke(this, variant);
    }

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _windows.Changed -= OnWindowsThemeChanged;
        _windows.Dispose();
    }
}

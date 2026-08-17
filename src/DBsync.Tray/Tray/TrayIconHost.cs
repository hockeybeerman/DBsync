using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DBsync.Tray.Theming;
using DBsync.Tray.ViewModels;
using Forms = System.Windows.Forms;

namespace DBsync.Tray.Tray;

/// <summary>
/// Owns the notification-area icon: its glyph, its rotation while syncing, its tooltip, and its
/// left- and right-click behaviour.
/// <para>
/// WPF has no tray-icon primitive, so this wraps WinForms' <see cref="Forms.NotifyIcon"/> — the
/// only reason the project references WindowsForms at all. The context menu is a WPF one so it
/// can be themed; a WinForms ContextMenuStrip would drag the system's own look in.
/// </para>
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly WindowsTheme _windowsTheme;
    private readonly DispatcherTimer _rotationTimer;
    private readonly Forms.NotifyIcon _icon;

    private GlyphIconFrames _frames;
    private ContextMenu? _menu;
    private int _frame;
    private bool _disposed;

    public TrayIconHost(ShellViewModel shell, WindowsTheme windowsTheme)
    {
        _shell = shell;
        _windowsTheme = windowsTheme;

        _frames = GlyphIconFrames.Render(WindowsTheme.Shell);

        _icon = new Forms.NotifyIcon
        {
            Icon = _frames.Still,
            Visible = true,
            Text = "DBsync",
        };

        _icon.MouseUp += OnMouseUp;

        _rotationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = GlyphIconFrames.FrameInterval,
        };
        _rotationTimer.Tick += OnRotationTick;

        _shell.PropertyChanged += OnShellPropertyChanged;
        _windowsTheme.ShellChanged += OnShellThemeChanged;

        UpdateTooltip();
        UpdateRotation();
    }

    /// <summary>Left-click: toggle the flyout.</summary>
    public event Action? ToggleRequested;

    /// <summary>Context menu: Open.</summary>
    public event Action? OpenRequested;

    /// <summary>Context menu: Pause / Resume.</summary>
    public event Action? PauseRequested;

    /// <summary>Context menu: Quit.</summary>
    public event Action? QuitRequested;

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        switch (e.Button)
        {
            case Forms.MouseButtons.Left:
                ToggleRequested?.Invoke();
                break;
            case Forms.MouseButtons.Right:
                ShowContextMenu();
                break;
        }
    }

    /// <summary>
    /// A themed WPF menu, opened at the cursor. It lives in its own popup root rather than the
    /// flyout's tree, so its DynamicResource lookups resolve against Application.Resources —
    /// which is where the theme dictionary lives, so it follows an appearance change like
    /// everything else.
    /// </summary>
    private void ShowContextMenu()
    {
        _menu ??= BuildMenu();

        // Rebuilt label each time: the item reads Pause or Resume depending on current state.
        if (_menu.Items[1] is MenuItem pause)
            pause.Header = _shell.PausedAll ? "Resume syncing" : "Pause syncing";

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu
        {
            Style = Application.Current.TryFindResource("TrayContextMenuStyle") as Style,
        };

        var open = new MenuItem { Header = "Open DBsync" };
        open.Click += (_, _) => OpenRequested?.Invoke();

        var pause = new MenuItem { Header = "Pause syncing" };
        pause.Click += (_, _) => PauseRequested?.Invoke();

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        menu.Items.Add(open);
        menu.Items.Add(pause);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.AnySyncing):
                UpdateRotation();
                break;
            case nameof(ShellViewModel.StatusLine):
            case nameof(ShellViewModel.IsConnected):
                UpdateTooltip();
                break;
        }
    }

    /// <summary>Re-render the frames when the taskbar's ground flips, so the glyph stays visible.</summary>
    private void OnShellThemeChanged(object? sender, ThemeVariant variant)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || variant == _frames.ShellTheme) return;

            var replacement = GlyphIconFrames.Render(variant);
            var previous = _frames;
            _frames = replacement;

            _icon.Icon = _shell.AnySyncing ? _frames[_frame] : _frames.Still;
            previous.Dispose();
        });
    }

    private void UpdateRotation()
    {
        if (_shell.AnySyncing)
        {
            if (!_rotationTimer.IsEnabled) _rotationTimer.Start();
            return;
        }

        _rotationTimer.Stop();
        _frame = 0;
        if (!_disposed) _icon.Icon = _frames.Still;
    }

    private void OnRotationTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        _frame = (_frame + 1) % GlyphIconFrames.FrameCount;
        _icon.Icon = _frames[_frame];
    }

    /// <summary>
    /// The tooltip carries the same header copy the flyout shows, so hovering answers the
    /// question without opening anything.
    /// </summary>
    private void UpdateTooltip()
    {
        if (_disposed) return;

        var status = _shell.IsConnected ? _shell.StatusLine : "DBsync service is not running";

        // Shell_NotifyIcon truncates past 127 characters and silently fails on some builds if the
        // string is longer, so keep it inside the limit.
        var text = $"DBsync — {status}";
        _icon.Text = text.Length <= 127 ? text : text[..127];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shell.PropertyChanged -= OnShellPropertyChanged;
        _windowsTheme.ShellChanged -= OnShellThemeChanged;

        _rotationTimer.Stop();
        _rotationTimer.Tick -= OnRotationTick;

        _icon.MouseUp -= OnMouseUp;
        _icon.Visible = false;
        _icon.Dispose();

        _frames.Dispose();
    }
}

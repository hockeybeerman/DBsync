using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using DBsync.Contracts;
using DBsync.Tray.ViewModels;

namespace DBsync.Tray.Views;

public partial class FlyoutWindow : Window
{
    /// <summary>Panel inset from the work area's right edge and from the taskbar.</summary>
    private const double Inset = 16;

    /// <summary>Slack around the panel so the drop shadow has somewhere to paint.</summary>
    private const double ShadowMargin = 24;

    private readonly ShellViewModel _shell;
    private Storyboard? _rotation;
    private bool _rowMenuOpen;

    public FlyoutWindow(ShellViewModel shell)
    {
        _shell = shell;
        InitializeComponent();

        DataContext = shell;
        shell.PropertyChanged += OnShellPropertyChanged;

        SourceInitialized += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Raised when a row is clicked, so the host can route to the right surface.</summary>
    public event Action<PairViewModel>? PairActivated;

    /// <summary>Raised for the header and footer buttons that open other surfaces.</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>Raised when a row's context menu asks to remove that pair.</summary>
    public event Action<PairViewModel>? PairRemoveRequested;

    public void ShowFlyout()
    {
        Show();
        Activate();
        Reposition();
        PlayEntry();
        UpdateRotation();
    }

    public void HideFlyout()
    {
        StopRotation();
        Hide();
    }

    public void ToggleFlyout()
    {
        if (IsVisible) HideFlyout();
        else ShowFlyout();
    }

    /// <summary>
    /// Bottom-right of the work area, which already excludes the taskbar — so "16px above the
    /// taskbar" is just 16px above the work area's bottom edge. The window is larger than the
    /// panel by the shadow margin on each side, so that is added back.
    /// </summary>
    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth + ShadowMargin - Inset;
        Top = work.Bottom - ActualHeight + ShadowMargin - Inset;
    }

    /// <summary>Rise 10px with a fade over 140ms ease-out, per the design.</summary>
    private void PlayEntry()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(140);

        RiseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });

        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.AnySyncing)) UpdateRotation();
    }

    /// <summary>
    /// The brand glyph turns once every 3.2s while anything is syncing, and holds still
    /// otherwise — the design is explicit that it is static when paused.
    /// </summary>
    private void UpdateRotation()
    {
        if (!IsVisible || !_shell.AnySyncing)
        {
            StopRotation();
            return;
        }

        if (_rotation is not null) return;

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3.2))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };

        _rotation = new Storyboard();
        _rotation.Children.Add(spin);
        Storyboard.SetTarget(spin, BrandGlyph);
        Storyboard.SetTargetProperty(spin,
            new PropertyPath("RenderTransform.(RotateTransform.Angle)"));
        _rotation.Begin(this, true);
    }

    private void StopRotation()
    {
        if (_rotation is null) return;

        _rotation.Stop(this);
        _rotation = null;
        BrandRotation.Angle = 0;
    }

    private void OnPairRow(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PairViewModel pair }) PairActivated?.Invoke(pair);
    }

    private void OnRemovePair(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: PairViewModel pair }) PairRemoveRequested?.Invoke(pair);
    }

    /// <summary>
    /// The flyout dismisses on deactivate, and opening a context menu takes activation with it —
    /// which would close the panel out from under the menu the user just opened. Hold it open
    /// while a row menu is up.
    /// </summary>
    private void OnRowMenuOpened(object sender, RoutedEventArgs e) => _rowMenuOpen = true;

    private void OnRowMenuClosed(object sender, RoutedEventArgs e)
    {
        _rowMenuOpen = false;

        // Focus went to the menu, so the flyout is no longer active. Take it back, otherwise the
        // panel sits there ignoring the next click somewhere else.
        if (IsVisible && !IsActive && !StayOpenOnDeactivate) HideFlyout();
    }

    private void OnActivity(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("activity");

    private void OnSettings(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("settings");

    private void OnAddPair(object sender, RoutedEventArgs e) => NavigationRequested?.Invoke("wizard");

    /// <summary>
    /// Set by <c>--pin</c> to keep the flyout open when focus leaves. Development only: it exists
    /// so the panel can be inspected and captured while something else drives the service.
    /// </summary>
    public bool StayOpenOnDeactivate { get; set; }

    /// <summary>A flyout dismisses when focus leaves it, the way the shell's own flyouts do.</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible && !StayOpenOnDeactivate && !_rowMenuOpen) HideFlyout();
    }

    /// <summary>Escape closes it too.</summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == System.Windows.Input.Key.Escape) HideFlyout();
    }

    /// <summary>
    /// The tray app outlives its windows — closing the flyout must hide it, not tear it down,
    /// or the next click on the tray icon would have nothing to show.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideFlyout();
            return;
        }

        _shell.PropertyChanged -= OnShellPropertyChanged;
        base.OnClosing(e);
    }

    /// <summary>Set by the host when the app is genuinely quitting.</summary>
    public bool AllowClose { get; set; }
}

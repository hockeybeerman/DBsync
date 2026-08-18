using System.Windows;
using System.Windows.Media;

namespace DBsync.Tray.Views;

/// <summary>
/// The dim ground behind a modal dialog.
/// <para>
/// The design draws the conflict dialog over a backdrop covering "the whole app surface". WPF has
/// no in-window modal layer, so this is a transparent click-through-proof window pinned to the
/// bounds of the window being blocked — which keeps the dimming to the app's own surface rather
/// than the user's whole desktop.
/// </para>
/// </summary>
public sealed class BackdropWindow : Window
{
    private readonly Window _target;

    public BackdropWindow(Window target)
    {
        _target = target;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Owner = target;

        // neutral-900 at 50%, the same value in both palettes — the design specifies the literal
        // colour here rather than a role.
        Background = new SolidColorBrush(Color.FromArgb(0x80, 0x29, 0x2B, 0x31));

        Track();

        _target.LocationChanged += OnTargetMoved;
        _target.SizeChanged += OnTargetResized;
        _target.StateChanged += OnTargetStateChanged;
    }

    private void Track()
    {
        Left = _target.Left;
        Top = _target.Top;
        Width = _target.ActualWidth > 0 ? _target.ActualWidth : _target.Width;
        Height = _target.ActualHeight > 0 ? _target.ActualHeight : _target.Height;
    }

    private void OnTargetMoved(object? sender, EventArgs e) => Track();

    private void OnTargetResized(object sender, SizeChangedEventArgs e) => Track();

    /// <summary>Follow the window down when it is minimised, rather than floating over nothing.</summary>
    private void OnTargetStateChanged(object? sender, EventArgs e) =>
        Visibility = _target.WindowState == WindowState.Minimized ? Visibility.Hidden : Visibility.Visible;

    protected override void OnClosed(EventArgs e)
    {
        _target.LocationChanged -= OnTargetMoved;
        _target.SizeChanged -= OnTargetResized;
        _target.StateChanged -= OnTargetStateChanged;
        base.OnClosed(e);
    }
}

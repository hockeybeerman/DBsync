using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using DBsync.Tray.ViewModels;

namespace DBsync.Tray.Views;

public partial class ActivityWindow : Window
{
    /// <summary>Start fetching the next page this far from the bottom, so scrolling stays smooth.</summary>
    private const double PrefetchMargin = 400;

    private readonly ActivityViewModel _activity;

    public ActivityWindow(ActivityViewModel activity)
    {
        _activity = activity;
        InitializeComponent();

        DataContext = activity;
        activity.ExportRequested += () => ExportRequested?.Invoke();
        activity.ReviewConflictsRequested += () => ReviewConflictsRequested?.Invoke();

        Loaded += async (_, _) => await activity.InitialiseAsync();
    }

    public event Action? ExportRequested;
    public event Action? ReviewConflictsRequested;

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // Double-click the title bar to maximise, as a normal window would.
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void OnMinimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Starts a native resize from one of the card's edge strips. Handing the drag to Windows is
    /// what keeps snapping, the minimum size and per-monitor DPI working; doing the arithmetic
    /// here would reimplement all three, badly.
    /// </summary>
    private void OnResizeGrip(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement grip || grip.Tag is not string edge) return;

        var hit = edge switch
        {
            "Left" => HtLeft,
            "Right" => HtRight,
            "Top" => HtTop,
            "TopLeft" => HtTopLeft,
            "TopRight" => HtTopRight,
            "Bottom" => HtBottom,
            "BottomLeft" => HtBottomLeft,
            "BottomRight" => HtBottomRight,
            _ => 0,
        };

        if (hit == 0) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        // The mouse has to be let go before the system takes over, or its modal resize loop never
        // sees the moves.
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, (IntPtr)hit, IntPtr.Zero);
        e.Handled = true;
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void OnClearSearch(object sender, RoutedEventArgs e) => _activity.ClearSearch();

    private void OnRange24(object sender, RoutedEventArgs e) => _activity.SetRange(24);

    private void OnRange7(object sender, RoutedEventArgs e) => _activity.SetRange(24 * 7);

    private void OnRange30(object sender, RoutedEventArgs e) => _activity.SetRange(24 * 30);

    /// <summary>
    /// Paging is driven from the scroll position rather than a "load more" button — the design's
    /// table has no such control, and a month of history should feel like one continuous list.
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer) return;
        if (viewer.ScrollableHeight <= 0) return;
        if (viewer.VerticalOffset < viewer.ScrollableHeight - PrefetchMargin) return;

        _ = _activity.LoadPageAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Stop taking live rows for a window that is going away.
        _activity.Detach();
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
        if (e.Key == Key.F5) _activity.RefreshCommand.Execute(null);
    }
}

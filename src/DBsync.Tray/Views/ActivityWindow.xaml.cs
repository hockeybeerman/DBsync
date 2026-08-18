using System.ComponentModel;
using System.Windows;
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

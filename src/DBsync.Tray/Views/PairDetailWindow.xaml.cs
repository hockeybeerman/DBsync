using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DBsync.Tray.ViewModels;

namespace DBsync.Tray.Views;

public partial class PairDetailWindow : Window
{
    private readonly PairDetailViewModel _detail;

    public PairDetailWindow(PairDetailViewModel detail)
    {
        _detail = detail;
        InitializeComponent();

        DataContext = detail;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // The pair can disappear from under this window - removed here, from another surface, or
        // by the CLI. Showing detail for something that no longer exists is worse than closing.
        detail.CloseRequested += () => Dispatcher.BeginInvoke(new Action(Close));

        Loaded += (_, _) => PlayEntry();
    }

    private void PlayEntry()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(120);

        RiseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnEdit(object sender, RoutedEventArgs e) => _detail.RequestEdit();

    private void OnHistory(object sender, RoutedEventArgs e) => _detail.RequestActivity();

    private void OnRemove(object sender, RoutedEventArgs e) => _detail.RequestRemove();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }
}

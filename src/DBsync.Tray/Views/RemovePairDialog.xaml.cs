using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DBsync.Tray.Notifications;
using DBsync.Tray.ViewModels;

namespace DBsync.Tray.Views;

public partial class RemovePairDialog : Window
{
    private readonly RemovePairViewModel _removal;

    public RemovePairDialog(RemovePairViewModel removal)
    {
        _removal = removal;
        InitializeComponent();

        DataContext = removal;
        removal.Finished += OnFinished;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        Loaded += (_, _) => PlayEntry();
    }

    /// <summary>Fires with the toast to show, or null if the user backed out.</summary>
    public event Action<ToastMessage?>? Completed;

    private void PlayEntry()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(120);

        RiseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _removal.Cancel();

    private void OnFinished(ToastMessage? toast)
    {
        _removal.Finished -= OnFinished;
        Completed?.Invoke(toast);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) _removal.Cancel();
    }
}

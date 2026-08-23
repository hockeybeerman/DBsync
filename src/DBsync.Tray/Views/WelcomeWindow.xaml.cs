using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DBsync.Tray.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        Loaded += (_, _) => PlayEntry();
    }

    /// <summary>
    /// True when the user asked to add a pair, false when they dismissed. Either way the welcome
    /// is done: dismissing is a decision, not a postponement, and asking again on every launch
    /// would be the kind of nagging this app is meant to avoid.
    /// </summary>
    public event Action<bool>? Completed;

    private void PlayEntry()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(120);

        RiseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    private void OnAddPair(object sender, RoutedEventArgs e) => Finish(true);

    private void OnDismiss(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool addPair)
    {
        Completed?.Invoke(addPair);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Finish(false);
    }
}

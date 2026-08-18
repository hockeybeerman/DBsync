using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DBsync.Contracts;
using DBsync.Tray.ViewModels;

namespace DBsync.Tray.Views;

public partial class WizardWindow : Window
{
    /// <summary>
    /// Clearance from the work area's right edge. The design puts the wizard at right: 424px so
    /// it never overlaps the 384px flyout and its 16px inset.
    /// </summary>
    private const double FlyoutClearance = 424;

    private const double ShadowMargin = 24;

    private readonly WizardViewModel _wizard;

    public WizardWindow(WizardViewModel wizard)
    {
        _wizard = wizard;
        InitializeComponent();

        DataContext = wizard;
        wizard.Finished += OnFinished;

        SourceInitialized += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => PlayEntry();
    }

    /// <summary>Fires with the saved pair, or null if the user cancelled.</summary>
    public event Action<FolderPair?>? Completed;

    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth + ShadowMargin - FlyoutClearance;
        Top = work.Top + Math.Max(0, (work.Height - ActualHeight) / 2);
    }

    private void PlayEntry()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(160);

        RiseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => OnFinished(null);

    private void OnPickRecent(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string path }) _wizard.PickRecent(path);
    }

    /// <summary>
    /// PasswordBox deliberately has no bindable Password property, so that a password never sits
    /// in a binding's value store. Push it across by hand instead.
    /// </summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) _wizard.Password = box.Password;
    }

    /// <summary>
    /// Probe on blur, as the design specifies — the user has finished typing a path, and the
    /// answer takes a network round trip we should not make on every keystroke.
    /// </summary>
    private void OnDestinationBlur(object sender, RoutedEventArgs e)
    {
        if (_wizard.IsStep2) _ = _wizard.ValidateDestinationAsync();
    }

    private void OnFinished(FolderPair? pair)
    {
        _wizard.Finished -= OnFinished;
        Completed?.Invoke(pair);

        AllowClose = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) OnFinished(null);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            // The system close button and Alt+F4 route through the same cancel path as the
            // wizard's own Cancel, so the host always learns the outcome.
            e.Cancel = true;
            OnFinished(null);
            return;
        }

        base.OnClosing(e);
    }

    private bool AllowClose { get; set; }
}

using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DBsync.Tray.Notifications;

namespace DBsync.Tray.Views;

public partial class ToastWindow : Window
{
    /// <summary>The design's dwell time before a toast fades on its own.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _timer;

    public ToastWindow(ToastMessage message)
    {
        InitializeComponent();

        Message = message;
        TitleText.Text = message.Title;
        BodyText.Text = message.Body;

        _timer = new DispatcherTimer { Interval = Lifetime };
        _timer.Tick += (_, _) => Dismiss();

        Loaded += (_, _) =>
        {
            PlayEntry();
            _timer.Start();
        };
    }

    public ToastMessage Message { get; }

    /// <summary>Raised when the card is clicked, so the host can route to the relevant surface.</summary>
    public event Action<ToastMessage>? Clicked;

    /// <summary>Raised when the toast leaves, so the stack can close the gap.</summary>
    public event Action<ToastWindow>? Dismissed;

    private void PlayEntry()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(140);

        RiseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    /// <summary>Hovering holds the toast open — six seconds is not long to read and decide.</summary>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _timer.Stop();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _timer.Start();
    }

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        Clicked?.Invoke(Message);
        Dismiss();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Dismiss();

    public void Dismiss()
    {
        _timer.Stop();

        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            Dismissed?.Invoke(this);
            Close();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }
}

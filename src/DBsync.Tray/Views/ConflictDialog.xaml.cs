using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DBsync.Tray.Notifications;
using DBsync.Tray.ViewModels;

namespace DBsync.Tray.Views;

public partial class ConflictDialog : Window
{
    private readonly ConflictViewModel _conflict;
    private BackdropWindow? _backdrop;

    public ConflictDialog(ConflictViewModel conflict, Window? owner)
    {
        _conflict = conflict;
        InitializeComponent();

        DataContext = conflict;
        conflict.Finished += OnFinished;
        conflict.Resolved += message => Resolved?.Invoke(message);

        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _backdrop = new BackdropWindow(owner);
        }
        else
        {
            // Opened from the tray, with no app window to dim. The design's backdrop covers "the
            // whole app surface"; when there is no such surface, dimming the user's entire desktop
            // for a file decision would be more intrusive than the design intends.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }

        Loaded += (_, _) => PlayEntry();
    }

    /// <summary>Fires with the toast to show for each resolution.</summary>
    public event Action<ToastMessage>? Resolved;

    public void ShowWithBackdrop()
    {
        _backdrop?.Show();
        Show();
        Activate();
    }

    /// <summary>Rise 10px with a fade, 120ms — the dialog is quicker than the windows.</summary>
    private void PlayEntry()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(120);

        RiseTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(10, 0, duration) { EasingFunction = easing });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
    }

    private void OnFinished() => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Escape dismisses without deciding. Deliberately not bound to either resolution — a
        // stray keypress must never choose which copy of a file survives.
        if (e.Key == Key.Escape) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _conflict.Finished -= OnFinished;

        _backdrop?.Close();
        _backdrop = null;

        base.OnClosing(e);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DBsync.Tray.Notifications;
using DBsync.Tray.ViewModels;

namespace DBsync.Tray.Views;

public partial class CredentialsDialog : Window
{
    private readonly CredentialsViewModel _credentials;

    public CredentialsDialog(CredentialsViewModel credentials)
    {
        _credentials = credentials;
        InitializeComponent();

        DataContext = credentials;
        credentials.Finished += OnFinished;

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

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box) _credentials.Password = box.Password;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _credentials.Cancel();

    private void OnFinished(ToastMessage? toast)
    {
        _credentials.Finished -= OnFinished;

        // Clear the field rather than leaving a password sitting in a live control.
        PasswordField.Clear();

        Completed?.Invoke(toast);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) _credentials.Cancel();
    }
}

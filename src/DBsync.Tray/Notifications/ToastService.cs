using System.Windows;
using DBsync.Tray.Services;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DBsync.Tray.Notifications;

/// <summary>
/// Raises Windows toasts, falling back to an in-app card when the shell will not take them.
/// <para>
/// The design calls for real Windows toasts so they survive the app not being foreground and land
/// in Action Center. An unpackaged desktop app can only do that through a registered AUMID and a
/// Start-menu shortcut; <c>ToastNotificationManagerCompat</c> puts both in place on first use.
/// </para>
/// <para>
/// It can still fail — notifications switched off for the app, a policy-managed machine, Focus
/// Assist configured to suppress. Those are exactly the moments when silently dropping "your
/// conflict was resolved" would be worst, so anything that does not reach the shell is shown
/// in-app instead, using the card the design specifies.
/// </para>
/// </summary>
public sealed class ToastService : IDisposable
{
    private readonly Action<ToastMessage> _showInApp;
    private bool _shellToastsWork = true;
    private bool _subscribed;

    public ToastService(Action<ToastMessage> showInApp)
    {
        _showInApp = showInApp;

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnToastActivated;
            _subscribed = true;
            _shellToastsWork = ShellWillDisplayToasts();

            Diagnostics.Note($"toast service ready; shell will display = {_shellToastsWork}; " +
                             $"was launched by a toast = {ToastNotificationManagerCompat.WasCurrentProcessToastActivated()}");
        }
        catch (Exception)
        {
            // No shell notification support at all. Everything goes to the in-app card.
            _shellToastsWork = false;
        }
    }

    /// <summary>
    /// Whether the shell will actually <em>show</em> a toast, not merely accept one.
    /// <para>
    /// This has to be asked explicitly. When notifications are switched off — for this app, for
    /// the user, or by policy — <c>Show()</c> still succeeds and the toast is silently discarded,
    /// never even reaching the notification centre. Treating "no exception" as success would mean
    /// the user is told nothing at all when a conflict is resolved, which is the one outcome worth
    /// avoiding.
    /// </para>
    /// </summary>
    private static bool ShellWillDisplayToasts()
    {
        try
        {
            return ToastNotificationManagerCompat.CreateToastNotifier().Setting
                == Windows.UI.Notifications.NotificationSetting.Enabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Re-checks whether the shell is taking toasts. The setting can change while the app runs —
    /// the user turns notifications back on, or Focus Assist ends — and a tray app runs for days.
    /// </summary>
    public void RefreshDeliveryMode() => _shellToastsWork = ShellWillDisplayToasts();

    /// <summary>Raised when the user clicks a toast, with the kind it was raised for.</summary>
    public event Action<ToastKind>? Activated;

    /// <summary>True while toasts are reaching the shell rather than the in-app fallback.</summary>
    public bool UsingShellToasts => _shellToastsWork;

    public void Show(ToastMessage message)
    {
        // Cheap enough to ask every time, and it means turning notifications back on takes effect
        // immediately rather than at the next restart.
        RefreshDeliveryMode();

        if (_shellToastsWork && TryShowShellToast(message)) return;

        // Marshal, because this can be reached from a service push on a background thread.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => _showInApp(message)));
    }

    private bool TryShowShellToast(ToastMessage message)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("kind", message.Kind.ToString())
                .AddText(message.Title)
                .AddText(message.Body)
                .Show();

            Diagnostics.Note($"shell toast sent: {message.Kind}");
            return true;
        }
        catch (Exception)
        {
            // Once the shell has refused, stop trying: retrying per notification would add a
            // failed COM call to every single toast.
            _shellToastsWork = false;
            return false;
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        Diagnostics.Note($"toast activated, argument='{args.Argument}'");

        var arguments = ToastArguments.Parse(args.Argument);
        if (!arguments.TryGetValue("kind", out var raw))
        {
            Diagnostics.Note("activation carried no 'kind' argument");
            return;
        }

        if (!Enum.TryParse<ToastKind>(raw, out var kind))
        {
            Diagnostics.Note($"activation kind '{raw}' not recognised");
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Activated?.Invoke(kind)));
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
            _subscribed = false;
        }

        try
        {
            // Clears this app's toasts from Action Center on exit. Leaving "Syncing paused" in the
            // centre after the app has gone would outlive the thing it describes.
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception)
        {
            // Nothing registered, or the shell is unavailable. Nothing to clean up.
        }
    }
}

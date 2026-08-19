using System.Windows;
using DBsync.Tray.Notifications;

namespace DBsync.Tray.Views;

/// <summary>
/// Positions in-app toasts bottom-right, stacked upward.
/// <para>
/// The design is explicit that they sit <em>above</em> the flyout and must not overlap it, so the
/// stack starts above the flyout's reserved height whenever the flyout is showing and drops to the
/// normal inset when it is not.
/// </para>
/// </summary>
public sealed class ToastStack
{
    private const double Inset = 16;
    private const double Gap = 8.4;

    /// <summary>Shadow slack baked into each toast window, matching the other surfaces.</summary>
    private const double ShadowMargin = 24;

    private readonly List<ToastWindow> _open = new();
    private readonly Func<double> _reservedHeight;

    /// <param name="reservedHeight">
    /// Height to keep clear at the bottom — the flyout's, while it is visible.
    /// </param>
    public ToastStack(Func<double> reservedHeight) => _reservedHeight = reservedHeight;

    public event Action<ToastMessage>? Clicked;

    public void Show(ToastMessage message)
    {
        var toast = new ToastWindow(message);
        toast.Clicked += payload => Clicked?.Invoke(payload);
        toast.Dismissed += Remove;

        _open.Add(toast);

        // ShowActivated is false on the window, so a toast never steals focus from what the user
        // is doing — which for a notification would be worse than not showing it.
        toast.Show();
        toast.SizeChanged += (_, _) => Reflow();
        Reflow();
    }

    private void Remove(ToastWindow toast)
    {
        _open.Remove(toast);
        Reflow();
    }

    private void Reflow()
    {
        var work = SystemParameters.WorkArea;
        var bottom = work.Bottom - Inset - _reservedHeight();

        // Newest nearest the corner, older ones pushed up.
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var toast = _open[i];
            if (toast.ActualHeight <= 0) continue;

            toast.Left = work.Right - toast.ActualWidth + ShadowMargin - Inset;
            toast.Top = bottom - toast.ActualHeight;

            bottom -= toast.ActualHeight + Gap;
        }
    }

    public void CloseAll()
    {
        foreach (var toast in _open.ToList()) toast.Close();
        _open.Clear();
    }
}

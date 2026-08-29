using System.Windows;
using DBsync.Tray.Theming;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// The activity table's column widths, shared by the header and every row.
/// <para>
/// One object rather than a property on each row, because a ColumnDefinition inside a DataTemplate
/// binds against the row's own data context and cannot reach the window's view model through a
/// RelativeSource - a ColumnDefinition has no visual parent to walk. Handing every row the same
/// instance means a splitter drag on the header moves all of them in one layout pass.
/// </para>
/// </summary>
public sealed class ActivityColumns : ObservableObject
{
    /// <summary>Matches the theme's defaults, and what a reset returns to.</summary>
    private static readonly double[] Defaults = { 132, 110, 150, 96 };

    private const double Minimum = 60;

    private GridLength _time;
    private GridLength _event;
    private GridLength _pair;
    private GridLength _result;

    public ActivityColumns()
    {
        var saved = UserSettings.Current.ActivityColumnWidths;
        var widths = saved is { Count: 4 } ? saved : new List<double>(Defaults);

        _time = Pixels(widths[0], Defaults[0]);
        _event = Pixels(widths[1], Defaults[1]);
        _pair = Pixels(widths[2], Defaults[2]);
        _result = Pixels(widths[3], Defaults[3]);
    }

    public GridLength TimeWidth
    {
        get => _time;
        set => Set(ref _time, value);
    }

    public GridLength EventWidth
    {
        get => _event;
        set => Set(ref _event, value);
    }

    public GridLength PairWidth
    {
        get => _pair;
        set => Set(ref _pair, value);
    }

    public GridLength ResultWidth
    {
        get => _result;
        set => Set(ref _result, value);
    }

    /// <summary>
    /// Writes the current widths to the user's preferences. Called when the window closes rather
    /// than on every drag - a splitter raises a change per mouse move, and none of the
    /// intermediate ones are worth a file write.
    /// </summary>
    public void Save()
    {
        UserSettings.Current.ActivityColumnWidths = new List<double>
        {
            TimeWidth.Value,
            EventWidth.Value,
            PairWidth.Value,
            ResultWidth.Value,
        };

        UserSettings.Current.Save();
    }

    /// <summary>
    /// A stored width that is absent, zero, negative or absurd falls back to the default. A saved
    /// file is not a trusted input: a column restored at 0px would be invisible with no way to
    /// find its splitter and drag it back.
    /// </summary>
    private static GridLength Pixels(double value, double fallback) =>
        new(double.IsFinite(value) && value >= Minimum && value <= 1000 ? value : fallback);
}

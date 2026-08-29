using System.Globalization;
using DBsync.Contracts;
using DBsync.Tray.Icons;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// One row of the activity table. Purely a projection — the service decides what happened and
/// what to call it; this maps that onto the design's icon, colour and tag vocabulary.
/// </summary>
public sealed class ActivityRowViewModel
{
    public ActivityRowViewModel(ActivityEntry entry, ActivityColumns columns)
    {
        Entry = entry;
        Columns = columns;
    }

    /// <summary>The table's shared column widths, bound by this row's ColumnDefinitions.</summary>
    public ActivityColumns Columns { get; }

    public ActivityEntry Entry { get; }

    public long Id => Entry.Id;

    /// <summary>Local time, tabular figures. The log stores UTC.</summary>
    /// <summary>
    /// Date and time, in the local zone. The date is shown on every row rather than only when the
    /// range spans more than a day, so the column does not change shape when the range changes -
    /// and it can be narrowed by dragging if it is not wanted.
    /// </summary>
    public string Time => Entry.TimestampUtc.ToLocalTime()
        .ToString("MM/dd/yy hh:mm tt", CultureInfo.InvariantCulture);

    public string Event => Entry.Kind switch
    {
        ActivityEventKind.Sent => "Sent",
        ActivityEventKind.Received => "Received",
        ActivityEventKind.Conflict => "Conflict",
        ActivityEventKind.Deleted => "Deleted",
        ActivityEventKind.Retry => "Retry",
        _ => "Locked",
    };

    public IconKey EventIcon => Entry.Kind switch
    {
        ActivityEventKind.Sent => IconKey.ArrowUp,
        ActivityEventKind.Received => IconKey.ArrowDown,
        ActivityEventKind.Conflict => IconKey.WarningDiamond,
        ActivityEventKind.Deleted => IconKey.Trash,
        ActivityEventKind.Retry => IconKey.CloudSlash,
        _ => IconKey.LockSimple,
    };

    public string File => Entry.File;

    /// <summary>
    /// Denormalised on the service side, so history stays readable after a pair is deleted. Bind
    /// this rather than looking the id up against the current pair list.
    /// </summary>
    public string PairName => Entry.PairName;

    public string Result => Entry.Result switch
    {
        ActivityResult.Ok => "OK",
        ActivityResult.Pending => "Pending",
        ActivityResult.Offline => "Offline",
        ActivityResult.Skipped => "Skipped",
        _ => "Failed",
    };

    /// <summary>Attention states take the outlined tag; routine outcomes take the neutral one.</summary>
    public bool ResultNeedsAttention =>
        Entry.Result is ActivityResult.Pending or ActivityResult.Failed;

    /// <summary>
    /// The Win32 text the service captured on a failure. Surfaced as a tooltip rather than a
    /// column — it is long, rare, and would otherwise be dropped entirely.
    /// </summary>
    public string? Message => Entry.Message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Entry.Message);

    /// <summary>Full timestamp and any failure text, for the row tooltip.</summary>
    public string Tooltip
    {
        get
        {
            var stamp = Entry.TimestampUtc.ToLocalTime().ToString("dddd d MMMM, HH:mm:ss");
            var size = Entry.Bytes > 0 ? $"  ·  {FormatBytes(Entry.Bytes)}" : "";
            return HasMessage ? $"{stamp}{size}\n{Entry.Message}" : $"{stamp}{size}";
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:0.#} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / 1024d / 1024:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB"
        : $"{bytes} bytes";
}

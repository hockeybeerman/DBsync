using System.Windows.Media;

namespace DBsync.Tray.Icons;

/// <summary>
/// Maps each design icon to a Segoe Fluent Icons codepoint.
/// <para>
/// The design specifies Phosphor shipped as vector assets. We render from the system icon font
/// instead, which keeps third-party assets out of the repo at the cost of exact fidelity — the
/// shapes are close in meaning but not identical to Phosphor, and several have no direct
/// counterpart at all (noted per entry).
/// </para>
/// <para>
/// Codepoints are numeric rather than literal private-use characters: the glyphs are invisible
/// in most editors and diffs, and they do not survive a tool that is not encoding-aware. Each
/// value was confirmed by eye against the icon sheet in the theme gallery, not taken from
/// documentation — the two Segoe icon fonts disagree across several ranges.
/// </para>
/// </summary>
public static class IconGlyphs
{
    /// <summary>
    /// Segoe Fluent Icons ships with Windows 11; Segoe MDL2 Assets covers Windows 10. The
    /// codepoints used here exist in both.
    /// </summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Dictionary<IconKey, char> Map = new()
    {
        [IconKey.ShuffleSimple] = (char)0xE8B1,          // Shuffle
        [IconKey.ArrowsLeftRight] = (char)0xE8AB,        // Switch — stands in for the two-way arrow
        [IconKey.ArrowsClockwise] = (char)0xE72C,        // Refresh
        [IconKey.Check] = (char)0xE73E,                  // CheckMark
        [IconKey.CheckCircle] = (char)0xE930,            // Completed
        [IconKey.WarningDiamond] = (char)0xE7BA,         // Warning — a triangle, not a diamond
        [IconKey.CloudSlash] = (char)0xEB5E,             // NetworkOffline
        [IconKey.Pause] = (char)0xE769,
        [IconKey.Play] = (char)0xE768,
        [IconKey.Plus] = (char)0xE710,                   // Add
        [IconKey.GearSix] = (char)0xE713,                // Settings
        [IconKey.ClockCounterClockwise] = (char)0xE81C,  // History
        [IconKey.ListMagnifyingGlass] = (char)0xE721,    // Search
        [IconKey.Sun] = (char)0xE706,                    // Brightness
        [IconKey.Moon] = (char)0xE708,                   // QuietHours
        [IconKey.DesktopTower] = (char)0xE977,           // PC
        [IconKey.Folder] = (char)0xE8B7,
        [IconKey.FolderOpen] = (char)0xE838,             // FolderOpen
        [IconKey.Network] = (char)0xE968,                // NetworkTower
        [IconKey.HardDrive] = (char)0xEDA2,              // Hard drive
        [IconKey.ArrowUp] = (char)0xE74A,                // Up
        [IconKey.ArrowDown] = (char)0xE74B,              // Down
        [IconKey.Trash] = (char)0xE74D,                  // Delete
        [IconKey.LockSimple] = (char)0xE72E,             // Lock
        [IconKey.Export] = (char)0xEDE1,                 // Export
        [IconKey.X] = (char)0xE711,                      // Cancel
        [IconKey.Minus] = (char)0xE738,                  // Remove
        [IconKey.CaretRight] = (char)0xE76C,             // ChevronRight
        [IconKey.CaretDown] = (char)0xE70D,              // ChevronDown
        [IconKey.CaretUp] = (char)0xE70E,                // ChevronUp
    };

    public static string Glyph(IconKey key) =>
        Map.TryGetValue(key, out var glyph) ? glyph.ToString() : "";

    /// <summary>Hex codepoint, for the gallery sheet and for reporting an unmapped icon.</summary>
    public static string Codepoint(IconKey key) =>
        Map.TryGetValue(key, out var glyph) ? $"U+{(int)glyph:X4}" : "—";

    public static IEnumerable<IconKey> All => Map.Keys;
}

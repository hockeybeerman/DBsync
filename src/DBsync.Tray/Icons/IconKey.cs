namespace DBsync.Tray.Icons;

/// <summary>
/// Every icon the product surfaces use, named after the Phosphor glyph the design specifies so
/// the mapping back to <c>docs/README.md</c> §Assets stays obvious.
/// <para>
/// The design's asset list also names <c>squares-four</c>, <c>magnifying-glass</c>,
/// <c>wifi-high</c>, <c>speaker-high</c>, <c>monitor</c> and <c>hard-drives</c>. Those belong to
/// the prototype's simulated desktop and taskbar, which is explicitly scaffolding and must not
/// be implemented, so they are deliberately absent here.
/// </para>
/// </summary>
public enum IconKey
{
    /// <summary>Brand glyph. Rotates while any pair is syncing.</summary>
    ShuffleSimple,

    /// <summary>Between the two paths on a pair row, and in the wizard's title bar.</summary>
    ArrowsLeftRight,

    /// <summary>Status: Syncing.</summary>
    ArrowsClockwise,

    /// <summary>Status: In sync.</summary>
    Check,

    /// <summary>Wizard step 2, successful destination probe.</summary>
    CheckCircle,

    /// <summary>Status: Conflict, and the conflict dialog's title.</summary>
    WarningDiamond,

    /// <summary>Status: Waiting, and the activity log's Retry event.</summary>
    CloudSlash,

    Pause,
    Play,
    Plus,

    /// <summary>Settings button in the flyout header.</summary>
    GearSix,

    /// <summary>Activity button in the flyout header.</summary>
    ClockCounterClockwise,

    /// <summary>Activity window title bar.</summary>
    ListMagnifyingGlass,

    /// <summary>Appearance: Light.</summary>
    Sun,

    /// <summary>Appearance: Dark.</summary>
    Moon,

    /// <summary>Appearance: Match Windows.</summary>
    DesktopTower,

    Folder,

    /// <summary>Wizard step 1, Browse.</summary>
    FolderOpen,

    /// <summary>Wizard step 2, UNC path option.</summary>
    Network,

    /// <summary>Wizard step 2, mapped drive option.</summary>
    HardDrive,

    /// <summary>Activity log: Sent.</summary>
    ArrowUp,

    /// <summary>Activity log: Received.</summary>
    ArrowDown,

    /// <summary>Activity log: Deleted.</summary>
    Trash,

    /// <summary>Activity log: Locked.</summary>
    LockSimple,

    /// <summary>Activity log: Export log.</summary>
    Export,

    /// <summary>Window close.</summary>
    X,

    /// <summary>Window minimize.</summary>
    Minus,

    /// <summary>Advanced options, collapsed.</summary>
    CaretRight,

    /// <summary>Advanced options, expanded.</summary>
    CaretDown,

    CaretUp,
}

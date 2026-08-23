using DBsync.Contracts;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// One row of the flyout's pair list.
/// <para>
/// This is a projection, not a translation: <see cref="Detail"/> and the header copy arrive from
/// the service already worded, so nothing here re-derives product text. Status-dependent colour,
/// icon and tag styling are left to DataTriggers on <see cref="Status"/> in the row template, so
/// they keep reading live theme resources and follow an appearance change.
/// </para>
/// </summary>
public sealed class PairViewModel : ObservableObject
{
    private string _name = "";
    private string _localPath = "";
    private string _sharePath = "";
    private string _detail = "";
    private PairStatus _status;
    private int _percent;
    private bool _needsCredentials;
    private bool _enabled = true;

    public PairViewModel(FolderPair pair) => Apply(pair);

    public string Id { get; private set; } = "";

    public string Name
    {
        get => _name;
        private set => Set(ref _name, value);
    }

    public string LocalPath
    {
        get => _localPath;
        private set => Set(ref _localPath, value);
    }

    public string SharePath
    {
        get => _sharePath;
        private set => Set(ref _sharePath, value);
    }

    /// <summary>Line 3 — "Up to date · checked 2 minutes ago". Comes from the service verbatim.</summary>
    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public PairStatus Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            Raise(nameof(StatusTag));
            Raise(nameof(StatusIcon));
            Raise(nameof(ShowProgress));
        }
    }

    public int Percent
    {
        get => _percent;
        private set
        {
            if (Set(ref _percent, value)) Raise(nameof(ProgressFraction));
        }
    }

    /// <summary>Right-aligned tag text on line 1.</summary>
    public string StatusTag => Status switch
    {
        PairStatus.Syncing => "Syncing",
        PairStatus.InSync => "In sync",
        PairStatus.Conflict => "Conflict",
        PairStatus.Waiting => "Waiting",
        _ => "Paused",
    };

    /// <summary>
    /// Line 1's status glyph. A fixed status→icon mapping rather than product copy, so it is
    /// safe to resolve here; the icon's colour stays in the template, where it can follow a
    /// theme change.
    /// </summary>
    public Icons.IconKey StatusIcon => Status switch
    {
        PairStatus.Syncing => Icons.IconKey.ArrowsClockwise,
        PairStatus.InSync => Icons.IconKey.Check,
        PairStatus.Conflict => Icons.IconKey.WarningDiamond,
        PairStatus.Waiting => Icons.IconKey.CloudSlash,
        _ => Icons.IconKey.Pause,
    };

    /// <summary>
    /// The share refused the stored sign-in. Kept separate from Status, which stays Waiting: the
    /// design's five statuses are a closed set, and this is a reason rather than a sixth state.
    /// </summary>
    public bool NeedsCredentials
    {
        get => _needsCredentials;
        private set => Set(ref _needsCredentials, value);
    }

    /// <summary>
    /// Whether this pair is running on its own account. Distinct from <see cref="Status"/> being
    /// Paused, which is also true when everything is paused globally - the difference decides
    /// whether resuming everything will restart this pair.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        private set => Set(ref _enabled, value);
    }

    /// <summary>Line 4 exists only while syncing.</summary>
    public bool ShowProgress => Status == PairStatus.Syncing;

    /// <summary>0–1, for the progress fill's width via a Grid star split.</summary>
    public double ProgressFraction => Math.Clamp(Percent / 100d, 0d, 1d);

    public void Apply(FolderPair pair)
    {
        Id = pair.Id;
        Name = pair.Name;
        LocalPath = pair.LocalPath;
        SharePath = pair.SharePath;
        Detail = pair.Detail;
        Status = pair.Status;
        Percent = pair.Percent;
        NeedsCredentials = pair.NeedsCredentials;
        Enabled = pair.Enabled;
    }
}

using System.IO;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Tray.Notifications;
using DBsync.Tray.Services;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// Backs the conflict dialog.
/// <para>
/// This is the one surface in the app that decides which copy of a file survives, so it is
/// deliberately conservative: nothing is resolved until the user presses a button, the primary
/// action always names the copy it will keep, and the apply-to-all checkbox states its exact
/// count rather than an open-ended "all".
/// </para>
/// <para>
/// The service parks conflicts — a file with an open conflict is excluded from reconciliation
/// until it is resolved — so neither copy moves while this dialog is open. Nothing here needs to
/// guard against that.
/// </para>
/// </summary>
public sealed class ConflictViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly Queue<ConflictRecord> _queue = new();

    private ConflictRecord? _current;
    private bool _winnerIsLocal = true;
    private bool _applyToPair;
    private bool _busy;
    private int _remainingInPair;

    public ConflictViewModel(ServiceConnection connection)
    {
        _connection = connection;

        PickLocalCommand = new RelayCommand(() => WinnerIsLocal = true, () => !Busy);
        PickShareCommand = new RelayCommand(() => WinnerIsLocal = false, () => !Busy);
        KeepBothCommand = new RelayCommand(() => _ = ResolveAsync(ConflictResolution.KeepBoth), () => !Busy);
        ResolveCommand = new RelayCommand(
            () => _ = ResolveAsync(WinnerIsLocal ? ConflictResolution.KeepLocal : ConflictResolution.KeepShare),
            () => !Busy);
    }

    /// <summary>Raised when there is nothing left to decide and the dialog should close.</summary>
    public event Action? Finished;

    /// <summary>Raised after each resolution, with the toast to show for it.</summary>
    public event Action<ToastMessage>? Resolved;

    public bool HasConflict => _current is not null;

    /// <summary>Just the file name — the full relative path is in the tooltip.</summary>
    public string FileName => _current is null
        ? ""
        : Path.GetFileName(_current.RelativePath) is { Length: > 0 } leaf ? leaf : _current.RelativePath;

    public string RelativePath => _current?.RelativePath ?? "";

    public string SharePath => _current?.SharePath ?? "";

    public string PairName => _current?.PairName ?? "";

    // ---- The two cards ------------------------------------------------------

    public string LocalWhen => _current is null ? "" : When(_current.Local.ModifiedUtc);

    public string LocalDetail => _current is null ? "" : Detail(_current.Local);

    public string ShareWhen => _current is null ? "" : When(_current.Share.ModifiedUtc);

    public string ShareDetail => _current is null ? "" : Detail(_current.Share);

    public bool WinnerIsLocal
    {
        get => _winnerIsLocal;
        set
        {
            if (!Set(ref _winnerIsLocal, value)) return;
            Raise(nameof(WinnerIsShare));
            Raise(nameof(PrimaryLabel));
        }
    }

    public bool WinnerIsShare => !WinnerIsLocal;

    /// <summary>The primary button names the copy it keeps, so the action is never ambiguous.</summary>
    public string PrimaryLabel => WinnerIsLocal ? "Keep this PC" : "Keep network copy";

    // ---- Apply to the rest of the pair --------------------------------------

    public bool ApplyToPair
    {
        get => _applyToPair;
        set => Set(ref _applyToPair, value);
    }

    /// <summary>Other unresolved conflicts in the same pair. The checkbox hides when there are none.</summary>
    public int RemainingInPair
    {
        get => _remainingInPair;
        private set
        {
            if (!Set(ref _remainingInPair, value)) return;
            Raise(nameof(HasOthersInPair));
            Raise(nameof(ApplyToPairLabel));
        }
    }

    public bool HasOthersInPair => RemainingInPair > 0;

    public string ApplyToPairLabel => RemainingInPair == 1
        ? "Do this for the other 1 conflict in this pair"
        : $"Do this for the other {RemainingInPair} conflicts in this pair";

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            PickLocalCommand.RaiseCanExecuteChanged();
            PickShareCommand.RaiseCanExecuteChanged();
            KeepBothCommand.RaiseCanExecuteChanged();
            ResolveCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand PickLocalCommand { get; }
    public RelayCommand PickShareCommand { get; }
    public RelayCommand KeepBothCommand { get; }
    public RelayCommand ResolveCommand { get; }

    // ---- Loading ------------------------------------------------------------

    /// <summary>
    /// Loads open conflicts, optionally scoped to one pair, and shows the first. Conflicts are
    /// worked through one at a time so each decision is made about a named file.
    /// </summary>
    public async Task LoadAsync(string? pairId = null)
    {
        var response = await _connection
            .TryAsync(client => client.GetConflictsAsync(new ConflictQuery { PairId = pairId }))
            .ConfigureAwait(true);

        _queue.Clear();
        foreach (var conflict in response?.Conflicts ?? new List<ConflictRecord>()) _queue.Enqueue(conflict);

        Advance();
    }

    private void Advance()
    {
        _current = _queue.Count > 0 ? _queue.Dequeue() : null;

        if (_current is null)
        {
            Finished?.Invoke();
            return;
        }

        // Each conflict is a fresh decision: never carry the previous choice, or the apply-to-all
        // flag, into a different file.
        _winnerIsLocal = true;
        _applyToPair = false;
        RemainingInPair = _queue.Count(other => other.PairId == _current.PairId);

        RaiseAll();
    }

    private async Task ResolveAsync(ConflictResolution resolution)
    {
        if (_current is null) return;

        var conflict = _current;
        var applied = ApplyToPair;

        Busy = true;
        try
        {
            var ack = await _connection.TryAsync(client => client.ResolveConflictAsync(
                new ResolveConflictRequest
                {
                    ConflictId = conflict.Id,
                    Resolution = resolution,
                    ApplyToPair = applied,
                })).ConfigureAwait(true);

            if (ack is null)
            {
                // Not in the design's table, but silence here would read as success on the one
                // action in the app that can lose a file.
                Resolved?.Invoke(new ToastMessage(ToastKind.ConflictResolved, "Could not resolve",
                    "The DBsync service is not responding, so nothing was changed."));
                return;
            }

            Resolved?.Invoke(ToastFor(resolution, conflict));

            // The service resolved the rest of this pair itself; dropping them here keeps the
            // queue honest rather than asking about files that are already decided.
            if (applied) RemoveQueued(conflict.PairId);
        }
        finally
        {
            Busy = false;
        }

        Advance();
    }

    private void RemoveQueued(string pairId)
    {
        var survivors = _queue.Where(conflict => conflict.PairId != pairId).ToList();
        _queue.Clear();
        foreach (var conflict in survivors) _queue.Enqueue(conflict);
    }

    private static ToastMessage ToastFor(ConflictResolution resolution, ConflictRecord conflict)
    {
        var file = Path.GetFileName(conflict.RelativePath);

        return resolution == ConflictResolution.KeepBoth
            ? ToastCopy.BothCopiesKept(KeepBothName(file))
            : ToastCopy.ConflictResolved(resolution == ConflictResolution.KeepLocal, file);
    }

    /// <summary>
    /// Mirrors the name the service actually gives the renamed copy. Duplicated wording rather
    /// than a value the contract returns — if the toast ever disagrees with the file on disk, this
    /// is the line to fix.
    /// </summary>
    private static string KeepBothName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return $"{stem} ({Environment.UserName}, {Environment.MachineName}){extension}";
    }

    private void RaiseAll()
    {
        Raise(nameof(HasConflict));
        Raise(nameof(FileName));
        Raise(nameof(RelativePath));
        Raise(nameof(SharePath));
        Raise(nameof(PairName));
        Raise(nameof(LocalWhen));
        Raise(nameof(LocalDetail));
        Raise(nameof(ShareWhen));
        Raise(nameof(ShareDetail));
        Raise(nameof(WinnerIsLocal));
        Raise(nameof(WinnerIsShare));
        Raise(nameof(PrimaryLabel));
        Raise(nameof(ApplyToPair));
    }

    /// <summary>"Today, 14:02" / "Yesterday, 13:47" / "12 Aug, 09:15".</summary>
    private static string When(DateTimeOffset moment)
    {
        var local = moment.ToLocalTime();
        var day = local.Date;
        var today = DateTimeOffset.Now.Date;

        if (day == today) return $"Today, {local:HH:mm}";
        if (day == today.AddDays(-1)) return $"Yesterday, {local:HH:mm}";
        return $"{local:d MMM}, {local:HH:mm}";
    }

    /// <summary>"248 KB · dana".</summary>
    private static string Detail(ConflictSide side)
    {
        var owner = string.IsNullOrWhiteSpace(side.Owner) ? "unknown" : side.Owner;
        return $"{Size(side.Bytes)} · {owner}";
    }

    private static string Size(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024d / 1024 / 1024:0.#} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / 1024d / 1024:0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB"
        : $"{bytes} bytes";
}

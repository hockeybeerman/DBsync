using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Tray.Services;

namespace DBsync.Tray.ViewModels;

/// <summary>A choice in one of the activity window's filter dropdowns.</summary>
public sealed record FilterOption(string Label, string? Value)
{
    /// <summary>
    /// The closed ComboBox renders its selection through SelectionBoxItem, which falls back to
    /// ToString when the template does not carry a DisplayMemberPath across. A record's generated
    /// ToString would print the whole shape, so it is overridden here — which also keeps the
    /// control from sizing itself to that text.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>Backs the activity &amp; history window.</summary>
public sealed class ActivityViewModel : ObservableObject
{
    /// <summary>Rows fetched per request. Scrolling near the end asks for the next page.</summary>
    private const int PageSize = 200;

    private readonly ServiceConnection _connection;

    private string _subline = "";
    private string _sentTag = "0 files sent";
    private string _receivedTag = "0 received";
    private string _conflictsTag = "0 conflicts";
    private bool _loading;
    private bool _hasMore = true;
    private bool _isEmpty;
    private FilterOption _pairFilter = new("All folder pairs", null);
    private FilterOption _kindFilter = new("All events", null);
    private int _hours = 24;
    private string _search = "";
    private CancellationTokenSource? _searchDebounce;

    public ActivityViewModel(ServiceConnection connection)
    {
        _connection = connection;
        _connection.LogAppended += OnLogAppended;

        ExportCommand = new RelayCommand(() => ExportRequested?.Invoke());
        ReviewConflictsCommand = new RelayCommand(() => ReviewConflictsRequested?.Invoke());
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync());
    }

    public event Action? ExportRequested;
    public event Action? ReviewConflictsRequested;

    public ObservableCollection<ActivityRowViewModel> Rows { get; } = new();

    /// <summary>Column widths, shared with every row so a header drag moves the whole table.</summary>
    public ActivityColumns Columns { get; } = new();

    /// <summary>"Last 24 hours across 4 folder pairs" — recomputed from the live filter.</summary>
    public string Subline
    {
        get => _subline;
        private set => Set(ref _subline, value);
    }

    public string SentTag
    {
        get => _sentTag;
        private set => Set(ref _sentTag, value);
    }

    public string ReceivedTag
    {
        get => _receivedTag;
        private set => Set(ref _receivedTag, value);
    }

    public string ConflictsTag
    {
        get => _conflictsTag;
        private set => Set(ref _conflictsTag, value);
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    // ---- Filters ------------------------------------------------------------

    public ObservableCollection<FilterOption> PairOptions { get; } = new()
    {
        new FilterOption("All folder pairs", null),
    };

    public IReadOnlyList<FilterOption> KindOptions { get; } = new[]
    {
        new FilterOption("All events", null),
        new FilterOption("Sent", nameof(ActivityEventKind.Sent)),
        new FilterOption("Received", nameof(ActivityEventKind.Received)),
        new FilterOption("Conflict", nameof(ActivityEventKind.Conflict)),
        new FilterOption("Deleted", nameof(ActivityEventKind.Deleted)),
        new FilterOption("Retry", nameof(ActivityEventKind.Retry)),
        new FilterOption("Locked", nameof(ActivityEventKind.Locked)),
    };

    public FilterOption PairFilter
    {
        get => _pairFilter;
        set
        {
            if (value is null || !Set(ref _pairFilter, value)) return;
            _ = ReloadAsync();
        }
    }

    public FilterOption KindFilter
    {
        get => _kindFilter;
        set
        {
            if (value is null || !Set(ref _kindFilter, value)) return;
            _ = ReloadAsync();
        }
    }

    /// <summary>
    /// Free-text search over the file path and the folder pair's name.
    /// <para>
    /// Typing re-queries rather than filtering what is on screen, because the rows are paged: a
    /// filter applied here would search only what has been fetched so far and report nothing
    /// found for a file that is simply further down the history.
    /// </para>
    /// </summary>
    public string Search
    {
        get => _search;
        set
        {
            if (!Set(ref _search, value)) return;
            Raise(nameof(HasSearch));
            DebounceReload();
        }
    }

    public bool HasSearch => Search.Length > 0;

    public void ClearSearch() => Search = "";

    /// <summary>
    /// Waits for a pause in typing before querying. Without it every keystroke costs a round trip
    /// and the results flicker through partial words on the way to the one being typed.
    /// </summary>
    private async void DebounceReload()
    {
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = new CancellationTokenSource();

        var token = _searchDebounce.Token;
        try
        {
            // Started from the UI thread and deliberately not wrapped in Task.Run: the
            // continuation has to come back to the UI thread, because ReloadAsync rebuilds Rows
            // and a bound ObservableCollection cannot be touched from the thread pool. Doing that
            // throws into a task nobody awaits, where the failure is swallowed and the search
            // silently does nothing.
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later keystroke.
        }
    }

    public int Hours
    {
        get => _hours;
        private set
        {
            if (!Set(ref _hours, value)) return;
            Raise(nameof(IsLast24Hours));
            Raise(nameof(IsLast7Days));
            Raise(nameof(IsLast30Days));
            _ = ReloadAsync();
        }
    }

    public bool IsLast24Hours => Hours == 24;
    public bool IsLast7Days => Hours == 24 * 7;
    public bool IsLast30Days => Hours == 24 * 30;

    public void SetRange(int hours) => Hours = hours;

    /// <summary>
    /// Scopes the view to one pair. Used when the window is opened from a pair row, so it lands
    /// showing that pair's history rather than everything.
    /// </summary>
    public void SelectPair(string pairId)
    {
        var match = PairOptions.FirstOrDefault(option => option.Value == pairId);
        if (match is not null)
        {
            PairFilter = match;
            return;
        }

        // The options list may not have loaded yet; apply it once it has.
        _pendingPairSelection = pairId;
    }

    private string? _pendingPairSelection;

    public RelayCommand ExportCommand { get; }
    public RelayCommand ReviewConflictsCommand { get; }
    public RelayCommand RefreshCommand { get; }

    // ---- Loading ------------------------------------------------------------

    public async Task InitialiseAsync()
    {
        await LoadPairOptionsAsync().ConfigureAwait(true);
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task LoadPairOptionsAsync()
    {
        var state = await _connection.TryAsync(client => client.GetStateAsync()).ConfigureAwait(true);
        if (state is null) return;

        var selected = PairFilter.Value;
        PairOptions.Clear();
        PairOptions.Add(new FilterOption("All folder pairs", null));
        foreach (var pair in state.Pairs) PairOptions.Add(new FilterOption(pair.Name, pair.Id));

        // A selection requested before the options existed takes precedence; otherwise keep the
        // current one if its pair survives, and fall back to All if it does not.
        var wanted = _pendingPairSelection ?? selected;
        _pendingPairSelection = null;

        _pairFilter = PairOptions.FirstOrDefault(option => option.Value == wanted) ?? PairOptions[0];
        Raise(nameof(PairFilter));
    }

    public async Task ReloadAsync()
    {
        Rows.Clear();
        _hasMore = true;
        await LoadPageAsync().ConfigureAwait(true);
        await LoadSummaryAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Fetches the next page. Paging is server-side via <see cref="ActivityQuery.Offset"/>, so a
    /// month of history never has to come across the pipe at once.
    /// </summary>
    public async Task LoadPageAsync()
    {
        if (Loading || !_hasMore) return;

        Loading = true;
        try
        {
            var response = await _connection.TryAsync(client => client.GetActivityAsync(new ActivityQuery
            {
                PairId = PairFilter.Value,
                Since = TimeSpan.FromHours(Hours),
                Kind = SelectedKind,
                Search = Search,
                Limit = PageSize,
                Offset = Rows.Count,
            })).ConfigureAwait(true);

            if (response is null)
            {
                _hasMore = false;
                IsEmpty = Rows.Count == 0;
                return;
            }

            foreach (var entry in response.Entries) Rows.Add(new ActivityRowViewModel(entry, Columns));

            // Every filter is applied in SQL now, so a full page really does mean there is more.
            // While the kind filter was applied here, a page could arrive almost empty and this
            // count still had to be taken before filtering to stay honest.
            _hasMore = response.Entries.Count == PageSize;
            IsEmpty = Rows.Count == 0;
        }
        finally
        {
            Loading = false;
        }
    }

    private async Task LoadSummaryAsync()
    {
        var query = new ActivityQuery
        {
            PairId = PairFilter.Value,
            Since = TimeSpan.FromHours(Hours),
            Search = Search,
        };

        var summary = await _connection.TryAsync(client => client.GetActivitySummaryAsync(query))
            .ConfigureAwait(true);
        if (summary is null) return;

        SentTag = $"{Count(summary.FilesSent)} files sent";
        ReceivedTag = $"{Count(summary.FilesReceived)} received";
        ConflictsTag = summary.Conflicts == 1 ? "1 conflict" : $"{Count(summary.Conflicts)} conflicts";

        // The counters are scoped to the filter, so the subline has to be too — saying "across 4
        // folder pairs" while showing one pair's rows would misdescribe the numbers beside it.
        var pairCount = PairFilter.Value is null ? summary.PairCount : 1;
        var pairs = pairCount == 1 ? "1 folder pair" : $"{pairCount} folder pairs";
        Subline = $"{RangeLabel()} across {pairs}";
    }

    private string RangeLabel() => Hours switch
    {
        24 => "Last 24 hours",
        24 * 7 => "Last 7 days",
        24 * 30 => "Last 30 days",
        _ => $"Last {Hours} hours",
    };

    private static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>The kind filter as the service wants it, or null for every kind.</summary>
    private ActivityEventKind? SelectedKind =>
        KindFilter.Value is not null && Enum.TryParse<ActivityEventKind>(KindFilter.Value, out var kind)
            ? kind
            : null;

    private bool MatchesKind(ActivityEntry entry) =>
        SelectedKind is not { } kind || entry.Kind == kind;

    /// <summary>
    /// Whether a live row belongs in the current view. Mirrors the SQL rather than sharing it -
    /// a row arriving on the wire has never been near the database.
    /// </summary>
    private bool MatchesSearch(ActivityEntry entry) =>
        !HasSearch
        || entry.File.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || entry.PairName.Contains(Search, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A row arriving while the window is open goes straight to the top rather than triggering a
    /// re-query — the design's whole point is that this is live.
    /// </summary>
    private void OnLogAppended(ActivityEntry entry)
    {
        if (PairFilter.Value is not null && entry.PairId != PairFilter.Value) return;
        if (!MatchesKind(entry)) return;
        if (!MatchesSearch(entry)) return;

        Rows.Insert(0, new ActivityRowViewModel(entry, Columns));
        IsEmpty = false;

        // Counters have moved; refresh them rather than trying to increment the right one.
        _ = LoadSummaryAsync();
    }

    public void Detach()
    {
        Columns.Save();
        _connection.LogAppended -= OnLogAppended;
        _searchDebounce?.Cancel();
        _searchDebounce?.Dispose();
        _searchDebounce = null;
    }
}

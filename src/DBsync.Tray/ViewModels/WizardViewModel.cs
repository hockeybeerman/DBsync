using System.IO;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Tray.Icons;
using DBsync.Tray.Interop;
using DBsync.Tray.Services;
using DBsync.Tray.Theming;

namespace DBsync.Tray.ViewModels;

/// <summary>Backs the three-step add/edit wizard.</summary>
public sealed class WizardViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;
    private readonly UserSettings _settings;
    private readonly FolderPair? _editing;

    private int _step = 1;
    private string _localPath = "";
    private string _sharePath = @"\\server\share";
    private DestinationKind _destKind = DestinationKind.Unc;
    private string _username = "";
    private string _password = "";
    private bool _saveCredentials = true;
    private SyncDirection _direction = SyncDirection.TwoWay;
    private bool _watcher = true;
    private bool _advancedOpen;
    private string _excludes = "*.tmp, ~$*, node_modules/";
    private string _uploadLimit = "Unlimited";
    private string _versionsKept = "5";
    private bool _useVss;
    private string _validationMessage = "";
    private bool _validationOk;
    private bool _hasValidation;
    private bool _busy;
    private bool _checking;

    public WizardViewModel(ServiceConnection connection, UserSettings settings, FolderPair? editing = null)
    {
        _connection = connection;
        _settings = settings;
        _editing = editing;

        if (editing is not null) LoadFrom(editing);

        BrowseCommand = new RelayCommand(Browse);
        BackCommand = new RelayCommand(Back);
        NextCommand = new RelayCommand(() => _ = NextAsync(), () => !Busy);
        ToggleAdvancedCommand = new RelayCommand(() => AdvancedOpen = !AdvancedOpen);
        PickUncCommand = new RelayCommand(() => SetDestinationKind(DestinationKind.Unc));
        PickDriveCommand = new RelayCommand(() => SetDestinationKind(DestinationKind.Drive));
        PickRecentCommand = new RelayCommand(() => { });
    }

    /// <summary>Raised when the wizard should close, with the saved pair or null on cancel.</summary>
    public event Action<FolderPair?>? Finished;

    public bool IsEditing => _editing is not null;

    public string Title => IsEditing ? "Edit folder pair" : "Add folder pair";

    // ---- Step flow ----------------------------------------------------------

    public int Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;

            Raise(nameof(IsStep1));
            Raise(nameof(IsStep2));
            Raise(nameof(IsStep3));
            Raise(nameof(Summary));
            Raise(nameof(BackLabel));
            Raise(nameof(NextLabel));
            for (var i = 0; i < Steps.Count; i++) Steps[i].Active = _step >= i + 1;
        }
    }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    /// <summary>
    /// The indicator's three columns. Completed and current steps share a treatment, so Active is
    /// cumulative rather than an equality test.
    /// </summary>
    public IReadOnlyList<StepIndicatorItem> Steps { get; } = new[]
    {
        new StepIndicatorItem("Local folder") { Active = true },
        new StepIndicatorItem("Destination"),
        new StepIndicatorItem("Sync options"),
    };

    public string BackLabel => Step == 1 ? "Cancel" : "Back";

    public string NextLabel => Step == 3 ? (IsEditing ? "Save changes" : "Create pair") : "Continue";

    /// <summary>"Step N of 3" until the last step, then the pair itself.</summary>
    public string Summary => Step == 3 ? $"{LocalPath}  ⇄  {SharePath}" : $"Step {Step} of 3";

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) NextCommand.RaiseCanExecuteChanged();
        }
    }

    // ---- Step 1 -------------------------------------------------------------

    public string LocalPath
    {
        get => _localPath;
        set
        {
            if (!Set(ref _localPath, value)) return;

            // Editing clears a stale complaint, so the line does not keep objecting to a path
            // the user has already corrected.
            ClearValidation();
            Raise(nameof(Summary));
        }
    }

    public IReadOnlyList<string> RecentFolders => _settings.RecentLocalFolders;

    public bool HasRecentFolders => _settings.RecentLocalFolders.Count > 0;

    // ---- Step 2 -------------------------------------------------------------

    public DestinationKind DestKind
    {
        get => _destKind;
        private set
        {
            if (!Set(ref _destKind, value)) return;
            Raise(nameof(IsUnc));
            Raise(nameof(IsDrive));
            Raise(nameof(DestLabel));
        }
    }

    public bool IsUnc => DestKind == DestinationKind.Unc;
    public bool IsDrive => DestKind == DestinationKind.Drive;

    public string DestLabel => IsUnc ? "UNC path" : "Mapped drive path";

    public string SharePath
    {
        get => _sharePath;
        set
        {
            if (!Set(ref _sharePath, value)) return;

            // Any edit invalidates the previous probe; the line should not keep claiming a path
            // is reachable after it has been changed.
            ClearValidation();
            Raise(nameof(Summary));
        }
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    public bool SaveCredentials
    {
        get => _saveCredentials;
        set => Set(ref _saveCredentials, value);
    }

    /// <summary>The service's own validation copy, shown verbatim under the field.</summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set => Set(ref _validationMessage, value);
    }

    public bool ValidationOk
    {
        get => _validationOk;
        private set
        {
            if (!Set(ref _validationOk, value)) return;
            Raise(nameof(ValidationIcon));
        }
    }

    public bool HasValidation
    {
        get => _hasValidation;
        private set => Set(ref _hasValidation, value);
    }

    /// <summary>A probe is in flight. Drives the pending treatment on the validation line.</summary>
    public bool Checking
    {
        get => _checking;
        private set
        {
            if (!Set(ref _checking, value)) return;
            Raise(nameof(ValidationIcon));
        }
    }

    public IconKey ValidationIcon => Checking
        ? IconKey.ArrowsClockwise
        : ValidationOk ? IconKey.CheckCircle : IconKey.X;

    // ---- Step 3 -------------------------------------------------------------

    public SyncDirection Direction
    {
        get => _direction;
        set
        {
            if (!Set(ref _direction, value)) return;
            Raise(nameof(IsTwoWay));
            Raise(nameof(IsPush));
            Raise(nameof(IsPull));
        }
    }

    public bool IsTwoWay
    {
        get => Direction == SyncDirection.TwoWay;
        set { if (value) Direction = SyncDirection.TwoWay; }
    }

    public bool IsPush
    {
        get => Direction == SyncDirection.Push;
        set { if (value) Direction = SyncDirection.Push; }
    }

    public bool IsPull
    {
        get => Direction == SyncDirection.Pull;
        set { if (value) Direction = SyncDirection.Pull; }
    }

    public bool Watcher
    {
        get => _watcher;
        set => Set(ref _watcher, value);
    }

    public bool AdvancedOpen
    {
        get => _advancedOpen;
        set
        {
            if (!Set(ref _advancedOpen, value)) return;
            Raise(nameof(AdvancedChevron));
        }
    }

    public IconKey AdvancedChevron => AdvancedOpen ? IconKey.CaretDown : IconKey.CaretRight;

    public string Excludes
    {
        get => _excludes;
        set => Set(ref _excludes, value);
    }

    public string UploadLimit
    {
        get => _uploadLimit;
        set => Set(ref _uploadLimit, value);
    }

    public string VersionsKept
    {
        get => _versionsKept;
        set => Set(ref _versionsKept, value);
    }

    public bool UseVss
    {
        get => _useVss;
        set => Set(ref _useVss, value);
    }

    // ---- Commands -----------------------------------------------------------

    public RelayCommand BrowseCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand ToggleAdvancedCommand { get; }
    public RelayCommand PickUncCommand { get; }
    public RelayCommand PickDriveCommand { get; }
    public RelayCommand PickRecentCommand { get; }

    public void PickRecent(string path) => LocalPath = path;

    /// <summary>Native folder picker. WPF has none of its own before .NET 8.</summary>
    private void Browse()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the local folder to keep in sync",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (Directory.Exists(LocalPath)) dialog.SelectedPath = LocalPath;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) LocalPath = dialog.SelectedPath;
    }

    private void SetDestinationKind(DestinationKind kind)
    {
        if (DestKind == kind) return;

        DestKind = kind;
        ClearValidation();

        // Swapping the option swaps the example with it, so the field never shows a value in the
        // wrong shape for the option that is selected.
        if (kind == DestinationKind.Drive)
        {
            var mapped = DriveResolver.MappedDrives().FirstOrDefault();
            SharePath = mapped.Letter is { Length: > 0 } letter ? letter + @"\" : @"Z:\";
        }
        else
        {
            SharePath = @"\\server\share";
        }
    }

    private void Back()
    {
        if (Step == 1)
        {
            Finished?.Invoke(null);
            return;
        }

        Step--;
    }

    private async Task NextAsync()
    {
        switch (Step)
        {
            case 1:
                if (!ValidateLocal()) return;
                Step = 2;
                return;

            case 2:
                if (!await ValidateDestinationAsync().ConfigureAwait(true)) return;
                Step = 3;
                return;

            default:
                await SaveAsync().ConfigureAwait(true);
                return;
        }
    }

    private bool ValidateLocal()
    {
        var path = LocalPath.Trim();

        if (path.Length == 0)
        {
            FailValidation("Choose a local folder.");
            return false;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            FailValidation($"'{path}' is not an absolute path.");
            return false;
        }

        if (!Directory.Exists(path))
        {
            FailValidation($"{path} does not exist.");
            return false;
        }

        LocalPath = path;
        ValidationMessage = "";
        return true;
    }

    /// <summary>
    /// Resolves a mapped drive to UNC, then asks the service to probe it. The probe runs
    /// service-side deliberately: it is the account that will actually do the syncing, so its
    /// answer is the one that matters — a share this user can reach may be invisible to LocalSystem.
    /// </summary>
    public async Task<bool> ValidateDestinationAsync()
    {
        var entered = SharePath.Trim();
        if (entered.Length == 0)
        {
            FailValidation("Enter a destination path.");
            return false;
        }

        if (DestKind == DestinationKind.Drive)
        {
            var resolution = DriveResolver.ToUnc(entered);
            if (!resolution.Success)
            {
                FailValidation(resolution.Error ?? "That drive could not be resolved.");
                return false;
            }

            // Save the UNC path, not the letter — the service cannot see this session's drives.
            if (!string.Equals(resolution.Path, entered, StringComparison.OrdinalIgnoreCase))
            {
                _sharePath = resolution.Path;
                Raise(nameof(SharePath));
                Raise(nameof(Summary));
            }
        }

        // Reaching an offline host can take the better part of a minute to time out. Without a
        // pending state the user gets a greyed Continue and no explanation for it.
        Busy = true;
        Checking = true;
        HasValidation = true;
        ValidationOk = false;
        ValidationMessage = $"Checking {SharePath}…";

        try
        {
            var probe = await _connection.TryAsync(client => client.ProbeDestinationAsync(
                new ProbeDestinationRequest
                {
                    Path = SharePath,
                    Kind = DestKind,
                    Username = string.IsNullOrWhiteSpace(Username) ? null : Username,
                    Password = string.IsNullOrWhiteSpace(Password) ? null : Password,
                })).ConfigureAwait(true);

            if (probe is null)
            {
                FailValidation("The DBsync service is not running, so the destination cannot be checked.");
                return false;
            }

            HasValidation = true;
            ValidationOk = probe.Reachable && probe.Writable;
            ValidationMessage = probe.Message;
            return ValidationOk;
        }
        finally
        {
            Busy = false;
            Checking = false;
        }
    }

    private async Task SaveAsync()
    {
        Busy = true;
        try
        {
            var pair = _editing?.CloneSettings() ?? new FolderPair();

            pair.LocalPath = LocalPath.Trim();
            pair.SharePath = SharePath.Trim();
            pair.DestKind = pair.SharePath.StartsWith(@"\\", StringComparison.Ordinal)
                ? DestinationKind.Unc
                : DestinationKind.Drive;
            pair.Direction = Direction;
            pair.Watcher = Watcher;
            pair.UseVss = UseVss;
            pair.SaveCredentials = SaveCredentials && !string.IsNullOrWhiteSpace(Username);
            pair.Excludes = Excludes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            pair.UploadLimitBytesPerSecond = RateText.Parse(UploadLimit);
            pair.VersionsKept = int.TryParse(VersionsKept, out var versions) ? Math.Max(0, versions) : 5;

            // Leaving Name empty lets the service derive it from the folder, which keeps one
            // implementation of that rule rather than two that can drift.
            if (IsEditing) pair.Name = _editing!.Name;

            var request = new SavePairRequest
            {
                Pair = pair,
                Username = string.IsNullOrWhiteSpace(Username) ? null : Username,
                Password = string.IsNullOrWhiteSpace(Username) ? null : Password,
            };

            var response = await _connection.TryAsync(client =>
                IsEditing ? client.UpdatePairAsync(request) : client.AddPairAsync(request))
                .ConfigureAwait(true);

            if (response is null)
            {
                FailValidation("Could not save — the DBsync service is not responding.");
                Step = 2;
                return;
            }

            _settings.RememberLocalFolder(pair.LocalPath);
            _settings.Save();

            Finished?.Invoke(response.Pair);
        }
        finally
        {
            Busy = false;
        }
    }

    private void LoadFrom(FolderPair pair)
    {
        _localPath = pair.LocalPath;
        _sharePath = pair.SharePath;
        _destKind = pair.DestKind;
        _direction = pair.Direction;
        _watcher = pair.Watcher;
        _useVss = pair.UseVss;
        _saveCredentials = pair.SaveCredentials;
        _excludes = string.Join(", ", pair.Excludes);
        _uploadLimit = RateText.Format(pair.UploadLimitBytesPerSecond);
        _versionsKept = pair.VersionsKept.ToString();
    }

    private void ClearValidation()
    {
        HasValidation = false;
        ValidationOk = false;
        ValidationMessage = "";
    }

    private void FailValidation(string message)
    {
        HasValidation = true;
        ValidationOk = false;
        ValidationMessage = message;
    }
}

/// <summary>One column of the wizard's step indicator.</summary>
public sealed class StepIndicatorItem : ObservableObject
{
    private bool _active;

    public StepIndicatorItem(string label) => Label = label;

    public string Label { get; }

    public bool Active
    {
        get => _active;
        set => Set(ref _active, value);
    }
}

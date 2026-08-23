using System.Diagnostics;
using System.IO;
using DBsync.Tray.Interop;
using DBsync.Tray.Theming;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// Backs the settings window: the handful of choices that belong to the person rather than to a
/// folder pair.
/// <para>
/// Per-pair settings are not here - they belong on the pair (#9). What is here is everything that
/// would otherwise have no home: sign-in launch, appearance, and enough about the service to
/// answer "is it running, and as whom" without opening services.msc.
/// </para>
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    private bool _startWithWindows;
    private string _error = "";

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _startWithWindows = StartupRegistration.IsEnabled();

        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        // The header line and connection state are the shell's, so the window follows the service
        // going up or down while it is open rather than showing whatever was true when it opened.
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.IsConnected)
                or nameof(ShellViewModel.ServiceAccount))
            {
                Raise(nameof(ServiceState));
                Raise(nameof(ServiceAccount));
            }
        };
    }

    /// <summary>Appearance is bound straight to the shell, so both surfaces stay in step.</summary>
    public ShellViewModel Shell => _shell;

    /// <summary>
    /// Whether the tray app launches at sign-in. Per-user by design - see
    /// <see cref="StartupRegistration"/> for why this is not the machine-wide Run key.
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows == value) return;

            if (!StartupRegistration.Set(value))
            {
                // Do not move the checkbox. A toggle that flips and does nothing is worse than one
                // that refuses and says why.
                Error = "Windows would not let DBsync change this. Your account may be managed by policy.";
                Raise(nameof(StartWithWindows));
                return;
            }

            Error = "";
            Set(ref _startWithWindows, value);
        }
    }

    public string ServiceState => _shell.IsConnected ? "Running" : "Not running";

    /// <summary>
    /// Which Windows account the service is logged on as. Worth surfacing: it decides what a share
    /// sees when a pair has no stored credentials, and it is otherwise invisible.
    /// </summary>
    public string ServiceAccount => _shell.ServiceAccount.Length > 0
        ? _shell.ServiceAccount
        : "Unknown — the service has not reported in";

    public string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DBsync");

    public string Error
    {
        get => _error;
        private set
        {
            if (!Set(ref _error, value)) return;
            Raise(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;

    public RelayCommand OpenDataFolderCommand { get; }

    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            Process.Start(new ProcessStartInfo(DataFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            Error = "That folder could not be opened.";
        }
    }
}

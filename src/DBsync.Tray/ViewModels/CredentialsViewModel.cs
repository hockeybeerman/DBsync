using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Tray.Notifications;
using DBsync.Tray.Services;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// Backs the credential re-prompt: the share said no to what we had stored, and needs a working
/// sign-in before that pair can move again.
/// <para>
/// Credentials go straight to the service, which files them in Windows Credential Manager. They
/// are never written to config, never logged, and are not held here beyond the round trip.
/// </para>
/// </summary>
public sealed class CredentialsViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;

    private string _username = "";
    private string _password = "";
    private bool _busy;
    private string _error = "";

    public CredentialsViewModel(ServiceConnection connection, FolderPair pair)
    {
        _connection = connection;
        Pair = pair;

        SignInCommand = new RelayCommand(() => _ = SignInAsync(), () => !Busy && CanSubmit);
    }

    /// <summary>Raised when the dialog is done, with the toast to show, or null if cancelled.</summary>
    public event Action<ToastMessage?>? Finished;

    public FolderPair Pair { get; }

    public string PairName => Pair.Name;

    public string SharePath => Pair.SharePath;

    public string Username
    {
        get => _username;
        set
        {
            if (!Set(ref _username, value)) return;
            Error = "";
            SignInCommand.RaiseCanExecuteChanged();
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (!Set(ref _password, value)) return;
            Error = "";
            SignInCommand.RaiseCanExecuteChanged();
        }
    }

    private bool CanSubmit => !string.IsNullOrWhiteSpace(Username);

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            SignInCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Why the last attempt failed. Empty until one does.</summary>
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

    public RelayCommand SignInCommand { get; }

    public void Cancel() => Finished?.Invoke(null);

    private async Task SignInAsync()
    {
        Busy = true;
        try
        {
            var ack = await _connection.TryAsync(client => client.StoreCredentialsAsync(
                new StoreCredentialsRequest
                {
                    PairId = Pair.Id,
                    Username = Username.Trim(),
                    Password = Password,
                })).ConfigureAwait(true);

            if (ack is null)
            {
                Error = "The DBsync service is not responding, so nothing was saved.";
                return;
            }

            // Storing them is not the same as them working — the service retries immediately, and
            // the row will say so either way. Claiming success here would be guessing.
            Finished?.Invoke(ToastCopy.CredentialsSaved(PairName));
        }
        finally
        {
            Busy = false;
        }
    }
}

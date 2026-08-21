using DBsync.Contracts;
using DBsync.Tray.Notifications;
using DBsync.Tray.Services;

namespace DBsync.Tray.ViewModels;

/// <summary>
/// Backs the confirmation for removing a folder pair.
/// <para>
/// The wording matters more than the mechanics here. "Remove" is ambiguous in a sync tool - the
/// reasonable fear is that it deletes the files - so the dialog states what actually happens:
/// the pair stops syncing, its baseline and open conflicts are dropped, its activity history is
/// kept, and nothing is deleted from either folder. Every one of those is what
/// <c>SyncEngine.DeletePairAsync</c> really does.
/// </para>
/// </summary>
public sealed class RemovePairViewModel : ObservableObject
{
    private readonly ServiceConnection _connection;

    private bool _busy;
    private string _error = "";

    public RemovePairViewModel(ServiceConnection connection, PairViewModel pair)
    {
        _connection = connection;
        Pair = pair;

        RemoveCommand = new RelayCommand(() => _ = RemoveAsync(), () => !Busy);
    }

    /// <summary>Raised when the dialog is done, with the toast to show, or null if cancelled.</summary>
    public event Action<ToastMessage?>? Finished;

    public PairViewModel Pair { get; }

    public string PairName => Pair.Name;

    public string LocalPath => Pair.LocalPath;

    public string SharePath => Pair.SharePath;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            RemoveCommand.RaiseCanExecuteChanged();
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

    public RelayCommand RemoveCommand { get; }

    public void Cancel() => Finished?.Invoke(null);

    private async Task RemoveAsync()
    {
        Busy = true;
        try
        {
            var ack = await _connection.TryAsync(client => client.DeletePairAsync(Pair.Id))
                .ConfigureAwait(true);

            if (ack is null)
            {
                // Leave the dialog open. A destructive action that silently did nothing is worse
                // than one that says it could not be done.
                Error = "The DBsync service is not responding, so the pair was not removed.";
                return;
            }

            Finished?.Invoke(ToastCopy.PairRemoved(PairName));
        }
        finally
        {
            Busy = false;
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Threading;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;

namespace DBsync.Tray.Services;

/// <summary>
/// Keeps a live connection to the DBsync service and republishes its push events on the UI
/// thread.
/// <para>
/// <see cref="DBsyncClient"/> deliberately does not reconnect itself — that policy belongs to the
/// app. The tray app is a per-user process and the service is a machine-level one, so the service
/// can stop, crash or be upgraded underneath us at any time. This retries on a backoff and, on
/// every successful connect, re-subscribes and pulls a full state so the UI is correct again with
/// no user action.
/// </para>
/// </summary>
public sealed class ServiceConnection : IAsyncDisposable
{
    private static readonly int[] RetrySecondsLadder = { 1, 2, 5, 10, 20, 30 };

    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private DBsyncClient? _client;
    private Task? _loop;
    private int _retryIndex;

    /// <summary>Completed to cut a backoff wait short when the user asks to retry now.</summary>
    private TaskCompletionSource<bool> _retryNow =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ServiceConnection() => _dispatcher = Application.Current.Dispatcher;

    /// <summary>A full state arrived — repaint everything.</summary>
    public event Action<ServiceState>? StateReplaced;

    /// <summary>One pair moved. Carries the recomputed header copy so the tray never derives it.</summary>
    public event Action<PairChangedEvent>? PairChanged;

    /// <summary>Connected or lost. The banner in #12 hangs off this.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>A row was appended to the activity log.</summary>
    public event Action<ActivityEntry>? LogAppended;

    /// <summary>A conflict was parked and needs a decision.</summary>
    public event Action<ConflictRaisedEvent>? ConflictRaised;

    /// <summary>A destination went offline or came back, and why.</summary>
    public event Action<ReachabilityEvent>? ReachabilityChanged;

    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>
    /// True once a connection has succeeded at least once. Distinguishes "the service went away"
    /// from "we have never reached it", which need different things said to the user.
    /// </summary>
    public bool HasEverConnected { get; private set; }

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>
    /// Abandons the current backoff and reconnects immediately. The ladder climbs to 30 seconds,
    /// which is far too long to sit staring at a banner after starting the service by hand.
    /// </summary>
    public void RetryNow()
    {
        _retryIndex = 0;
        _retryNow.TrySetResult(true);
    }

    /// <summary>
    /// Runs an operation against the current client. Returns default and lets the reconnect loop
    /// deal with it if the service is not there — every caller is a UI gesture, and a dropped
    /// service should not surface as an exception dialog.
    /// </summary>
    public async Task<T?> TryAsync<T>(Func<DBsyncClient, Task<T>> operation)
    {
        var client = _client;
        if (client is null || !client.IsConnected) return default;

        try
        {
            return await operation(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or IpcRequestException
                                       or TimeoutException or InvalidOperationException)
        {
            return default;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var dropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                var client = await DBsyncClient.ConnectAsync(2000, ct).ConfigureAwait(false);
                _client = client;
                _retryIndex = 0;

                client.Disconnected += _ => dropped.TrySetResult(true);
                client.EventReceived += OnEventReceived;

                await client.SubscribeAsync(ct).ConfigureAwait(false);

                // Subscribe first, then read: an event racing in between is applied on top of a
                // state that is at least as old, never dropped.
                var state = await client.GetStateAsync(ct).ConfigureAwait(false);
                HasEverConnected = true;
                Post(() =>
                {
                    ConnectionChanged?.Invoke(true);
                    StateReplaced?.Invoke(state);
                });

                await dropped.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Service down, restarting, or refusing connections. Fall through and retry.
            }

            await TeardownAsync().ConfigureAwait(false);
            Post(() => ConnectionChanged?.Invoke(false));

            var delay = RetrySecondsLadder[Math.Min(_retryIndex, RetrySecondsLadder.Length - 1)];
            _retryIndex++;

            _retryNow = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                // Whichever comes first: the backoff elapsing, or the user pressing Retry.
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(delay), ct), _retryNow.Task)
                    .ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnEventReceived(IpcMessage message)
    {
        switch (message.Event)
        {
            case IpcEventKind.PairChanged:
                var changed = message.PayloadAs<PairChangedEvent>();
                Post(() => PairChanged?.Invoke(changed));
                break;

            // A structural change (pair added or removed, global pause) invalidates the whole
            // list, so take the state that came with it rather than patching row by row.
            case IpcEventKind.StateChanged:
                var state = message.PayloadAs<ServiceState>();
                Post(() => StateReplaced?.Invoke(state));
                break;

            case IpcEventKind.LogAppended:
                var appended = message.PayloadAs<LogAppendedEvent>().Entry;
                Post(() => LogAppended?.Invoke(appended));
                break;

            case IpcEventKind.ConflictRaised:
                var raised = message.PayloadAs<ConflictRaisedEvent>();
                Post(() => ConflictRaised?.Invoke(raised));
                break;

            case IpcEventKind.ReachabilityChanged:
                var reach = message.PayloadAs<ReachabilityEvent>();
                Post(() => ReachabilityChanged?.Invoke(reach));
                break;
        }
    }

    private async Task TeardownAsync()
    {
        var client = _client;
        _client = null;
        if (client is null) return;

        client.EventReceived -= OnEventReceived;
        try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* already gone */ }
    }

    private void Post(Action action)
    {
        if (_cts.IsCancellationRequested) return;
        _dispatcher.BeginInvoke(action);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await TeardownAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* shutting down */ }
        }

        _cts.Dispose();
    }
}

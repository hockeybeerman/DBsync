using System.Collections.Concurrent;
using System.IO.Pipes;

namespace DBsync.Contracts.Ipc;

/// <summary>
/// Client side of the service pipe. One connection carries request/response traffic and the
/// push-event stream, so a tray app needs exactly one of these. It does not reconnect on its
/// own — callers watch <see cref="Disconnected"/> and build a new client, which is what lets
/// the UI survive service restarts.
/// </summary>
public sealed class DBsyncClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly FrameReader _reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    private DBsyncClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new FrameReader(pipe);
    }

    /// <summary>Raised for every push event the service broadcasts, once <see cref="SubscribeAsync"/> has run.</summary>
    public event Action<IpcMessage>? EventReceived;

    /// <summary>Raised once when the pipe drops. Carries the failure, or null on a clean close.</summary>
    public event Action<Exception?>? Disconnected;

    public bool IsConnected => _pipe.IsConnected;

    /// <summary>Connects to the running service, or throws <see cref="TimeoutException"/> if it is not up.</summary>
    public static async Task<DBsyncClient> ConnectAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException(
                "DBsync service is not running or is not accepting connections on \\\\.\\pipe\\" +
                IpcProtocol.PipeName + ".");
        }

        var client = new DBsyncClient(pipe);
        client._pump = Task.Run(() => client.PumpAsync(client._cts.Token));
        return client;
    }

    public Task<ServiceState> GetStateAsync(CancellationToken ct = default) =>
        RequestAsync<ServiceState>(IpcRequestKind.GetState, null, ct);

    public Task<PairResponse> AddPairAsync(SavePairRequest request, CancellationToken ct = default) =>
        RequestAsync<PairResponse>(IpcRequestKind.AddPair, request, ct);

    public Task<PairResponse> UpdatePairAsync(SavePairRequest request, CancellationToken ct = default) =>
        RequestAsync<PairResponse>(IpcRequestKind.UpdatePair, request, ct);

    public Task<AckResponse> DeletePairAsync(string pairId, CancellationToken ct = default) =>
        RequestAsync<AckResponse>(IpcRequestKind.DeletePair, new PairIdRequest { PairId = pairId }, ct);

    public Task<AckResponse> SetPairEnabledAsync(string pairId, bool enabled, CancellationToken ct = default) =>
        RequestAsync<AckResponse>(IpcRequestKind.SetPairEnabled,
            new SetPairEnabledRequest { PairId = pairId, Enabled = enabled }, ct);

    public Task<ServiceState> PauseAllAsync(CancellationToken ct = default) =>
        RequestAsync<ServiceState>(IpcRequestKind.PauseAll, null, ct);

    public Task<ServiceState> ResumeAllAsync(CancellationToken ct = default) =>
        RequestAsync<ServiceState>(IpcRequestKind.ResumeAll, null, ct);

    public Task<AckResponse> SyncNowAsync(string? pairId = null, CancellationToken ct = default) =>
        RequestAsync<AckResponse>(IpcRequestKind.SyncNow, new PairIdRequest { PairId = pairId ?? "" }, ct);

    public Task<ActivityResponse> GetActivityAsync(ActivityQuery query, CancellationToken ct = default) =>
        RequestAsync<ActivityResponse>(IpcRequestKind.GetActivity, query, ct);

    public Task<ActivitySummary> GetActivitySummaryAsync(ActivityQuery query, CancellationToken ct = default) =>
        RequestAsync<ActivitySummary>(IpcRequestKind.GetActivitySummary, query, ct);

    public Task<ConflictsResponse> GetConflictsAsync(ConflictQuery query, CancellationToken ct = default) =>
        RequestAsync<ConflictsResponse>(IpcRequestKind.GetConflicts, query, ct);

    public Task<AckResponse> ResolveConflictAsync(ResolveConflictRequest request, CancellationToken ct = default) =>
        RequestAsync<AckResponse>(IpcRequestKind.ResolveConflict, request, ct);

    public Task<DestinationProbe> ProbeDestinationAsync(ProbeDestinationRequest request, CancellationToken ct = default) =>
        RequestAsync<DestinationProbe>(IpcRequestKind.ProbeDestination, request, ct);

    public Task<AckResponse> StoreCredentialsAsync(StoreCredentialsRequest request, CancellationToken ct = default) =>
        RequestAsync<AckResponse>(IpcRequestKind.StoreCredentials, request, ct);

    /// <summary>Opts this connection into the push-event stream.</summary>
    public Task<AckResponse> SubscribeAsync(CancellationToken ct = default) =>
        RequestAsync<AckResponse>(IpcRequestKind.Subscribe, null, ct);

    public async Task<T> RequestAsync<T>(IpcRequestKind kind, object? payload, CancellationToken ct)
        where T : new()
    {
        var message = IpcMessage.MakeRequest(kind, payload);
        var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[message.Id!] = completion;

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await FrameWriter.WriteAsync(_pipe, message, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            var response = await completion.Task.ConfigureAwait(false);

            if (response.Error is { Length: > 0 } error)
                throw new IpcRequestException(kind, error);

            return response.PayloadAs<T>();
        }
        finally
        {
            _pending.TryRemove(message.Id!, out _);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await _reader.ReadAsync(ct).ConfigureAwait(false);
                if (message is null) break;

                if (message.Type == IpcMessageType.Event)
                {
                    EventReceived?.Invoke(message);
                }
                else if (message.Id is { Length: > 0 } id && _pending.TryRemove(id, out var completion))
                {
                    completion.TrySetResult(message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            var reason = failure ?? (Exception)new IOException("DBsync service closed the connection.");
            foreach (var pending in _pending.Values) pending.TrySetException(reason);
            _pending.Clear();
            Disconnected?.Invoke(failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Disposing a half-dead pipe is not interesting.
        }

        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); } catch { /* pump failures already reported */ }
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>Thrown when the service answers a request with an error.</summary>
public sealed class IpcRequestException : Exception
{
    public IpcRequestException(IpcRequestKind kind, string message)
        : base($"{kind} failed: {message}") => Kind = kind;

    public IpcRequestKind Kind { get; }
}

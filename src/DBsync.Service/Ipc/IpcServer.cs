using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DBsync.Contracts;
using DBsync.Contracts.Ipc;
using DBsync.Service.Configuration;
using DBsync.Service.Engine;
using DBsync.Service.Storage;
using Microsoft.Extensions.Logging;

namespace DBsync.Service.Ipc;

/// <summary>
/// Serves the named pipe the tray app talks to. Each connection carries request/response traffic
/// and, once subscribed, the push-event stream — so a client that reconnects after a service
/// restart gets a fresh, complete picture without any extra handshake.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private const int MaxConnections = 16;

    /// <summary>Listeners held open concurrently. Each can serve one client at a time.</summary>
    private const int ListenerCount = 6;

    private readonly SyncEngine _engine;
    private readonly ConfigStore _config;
    private readonly ActivityLog _activity;
    private readonly ILogger<IpcServer> _log;

    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private CancellationTokenSource? _cts;
    private Task? _accept;

    public IpcServer(SyncEngine engine, ConfigStore config, ActivityLog activity, ILogger<IpcServer> log)
    {
        _engine = engine;
        _config = config;
        _activity = activity;
        _log = log;
    }

    public void Start(CancellationToken stopping)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);

        _engine.Published += OnEnginePublished;
        _activity.Appended += OnActivityAppended;

        // A pool of independent listeners rather than one accept loop: each keeps an instance in
        // the listening state, so a client connecting while another is being served never finds
        // the pipe busy, and one long-lived client (a tray app) cannot starve the rest.
        var listeners = Enumerable
            .Range(0, ListenerCount)
            .Select(index => Task.Run(() => ListenAsync(index, _cts.Token)))
            .ToArray();

        _accept = Task.WhenAll(listeners);
        _log.LogInformation(@"IPC listening on \\.\pipe\{Pipe} ({Count} listeners).",
            IpcProtocol.PipeName, ListenerCount);
    }

    // ---- Accept / connection lifetime ---------------------------------------

    private async Task ListenAsync(int index, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var connection = new Connection(pipe);
                _connections[connection.Id] = connection;
                pipe = null; // ServeAsync owns it from here.

                await ServeAsync(connection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _log.LogError(ex, "IPC listener {Index} failed; retrying shortly.", index);

                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        // Any interactive user may drive their own tray app, but only read/write — creating
        // further instances of the pipe would let a client impersonate the service.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        // The account hosting the service needs CreateNewInstance to open each further listener.
        // That is LocalSystem in production and a plain user when the exe is run from a console,
        // so take the right from the running identity rather than assuming.
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.Owner ?? identity.User;
        if (owner is not null)
        {
            security.AddAccessRule(new PipeAccessRule(owner,
                PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            MaxConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }

    private async Task ServeAsync(Connection connection, CancellationToken ct)
    {
        _log.LogDebug("IPC client {Id} connected.", connection.Id);

        try
        {
            var reader = new FrameReader(connection.Pipe);
            while (!ct.IsCancellationRequested)
            {
                var message = await reader.ReadAsync(ct).ConfigureAwait(false);
                if (message is null) break;
                if (message.Type != IpcMessageType.Request) continue;

                var response = await DispatchAsync(connection, message).ConfigureAwait(false);
                await connection.SendAsync(response, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (IOException)
        {
            // Client vanished mid-frame — normal when a tray app exits.
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IPC client {Id} dropped after an error.", connection.Id);
        }
        finally
        {
            _connections.TryRemove(connection.Id, out _);
            connection.Dispose();
            _log.LogDebug("IPC client {Id} disconnected.", connection.Id);
        }
    }

    // ---- Dispatch -----------------------------------------------------------

    private async Task<IpcMessage> DispatchAsync(Connection connection, IpcMessage request)
    {
        try
        {
            object? payload = request.Request switch
            {
                IpcRequestKind.Ping => new AckResponse { Message = "DBsync v" + _engine.GetState().ServiceVersion },
                IpcRequestKind.GetState => _engine.GetState(),
                IpcRequestKind.AddPair => new PairResponse
                {
                    Pair = await _engine.AddPairAsync(request.PayloadAs<SavePairRequest>()).ConfigureAwait(false),
                },
                IpcRequestKind.UpdatePair => new PairResponse
                {
                    Pair = await _engine.UpdatePairAsync(request.PayloadAs<SavePairRequest>()).ConfigureAwait(false),
                },
                IpcRequestKind.DeletePair => await DeleteAsync(request).ConfigureAwait(false),
                IpcRequestKind.SetPairEnabled => await SetEnabledAsync(request).ConfigureAwait(false),
                IpcRequestKind.PauseAll => _engine.SetGlobalPause(true),
                IpcRequestKind.ResumeAll => _engine.SetGlobalPause(false),
                IpcRequestKind.SyncNow => SyncNow(request),
                IpcRequestKind.GetActivity => new ActivityResponse
                {
                    Entries = _activity.Query(request.PayloadAs<ActivityQuery>()),
                },
                IpcRequestKind.GetActivitySummary =>
                    _activity.Summarise(request.PayloadAs<ActivityQuery>(), _config.Snapshot().Pairs.Count),
                IpcRequestKind.GetConflicts => new ConflictsResponse
                {
                    Conflicts = _engine.GetConflicts(request.PayloadAs<ConflictQuery>()),
                },
                IpcRequestKind.ResolveConflict =>
                    await _engine.ResolveConflictAsync(request.PayloadAs<ResolveConflictRequest>()).ConfigureAwait(false),
                IpcRequestKind.ProbeDestination => _engine.Probe(request.PayloadAs<ProbeDestinationRequest>()),
                IpcRequestKind.StoreCredentials => StoreCredentials(request),
                IpcRequestKind.Subscribe => Subscribe(connection),
                _ => throw new InvalidOperationException($"Unsupported request {request.Request}."),
            };

            return IpcMessage.MakeResponse(request, payload);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Request {Kind} failed.", request.Request);
            return IpcMessage.MakeError(request, ex.Message);
        }
    }

    private async Task<AckResponse> DeleteAsync(IpcMessage request)
    {
        var payload = request.PayloadAs<PairIdRequest>();
        await _engine.DeletePairAsync(payload.PairId).ConfigureAwait(false);
        return new AckResponse { Message = "Pair removed." };
    }

    private async Task<AckResponse> SetEnabledAsync(IpcMessage request)
    {
        var payload = request.PayloadAs<SetPairEnabledRequest>();
        await _engine.SetPairEnabledAsync(payload.PairId, payload.Enabled).ConfigureAwait(false);
        return new AckResponse { Message = payload.Enabled ? "Pair resumed." : "Pair paused." };
    }

    private AckResponse SyncNow(IpcMessage request)
    {
        var payload = request.PayloadAs<PairIdRequest>();
        _engine.SyncNow(payload.PairId);
        return new AckResponse { Message = "Rescan queued." };
    }

    private AckResponse StoreCredentials(IpcMessage request)
    {
        _engine.StoreCredentials(request.PayloadAs<StoreCredentialsRequest>());
        return new AckResponse { Message = "Credentials stored in Windows Credential Manager." };
    }

    private AckResponse Subscribe(Connection connection)
    {
        connection.Subscribed = true;
        return new AckResponse { Message = "Subscribed to push events." };
    }

    // ---- Broadcast ----------------------------------------------------------

    private void OnEnginePublished(IpcEventKind kind, object payload) =>
        Broadcast(IpcMessage.MakeEvent(kind, payload));

    private void OnActivityAppended(ActivityEntry entry) =>
        Broadcast(IpcMessage.MakeEvent(IpcEventKind.LogAppended, new LogAppendedEvent { Entry = entry }));

    private void Broadcast(IpcMessage message)
    {
        foreach (var connection in _connections.Values)
        {
            if (!connection.Subscribed) continue;

            // Fire and forget: a wedged client must never stall the sync engine's thread.
            _ = connection.SendAsync(message, CancellationToken.None)
                .ContinueWith(task =>
                    {
                        _log.LogDebug(task.Exception, "Dropping IPC client {Id}: push failed.", connection.Id);
                        _connections.TryRemove(connection.Id, out _);
                        connection.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _engine.Published -= OnEnginePublished;
        _activity.Appended -= OnActivityAppended;

        _cts?.Cancel();

        foreach (var connection in _connections.Values) connection.Dispose();
        _connections.Clear();

        if (_accept is not null)
        {
            try { await _accept.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts?.Dispose();
    }

    /// <summary>One connected client. Writes are serialised so pushes cannot interleave with responses.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private bool _disposed;

        public Connection(NamedPipeServerStream pipe) => Pipe = pipe;

        public Guid Id { get; } = Guid.NewGuid();
        public NamedPipeServerStream Pipe { get; }
        public volatile bool Subscribed;

        public async Task SendAsync(IpcMessage message, CancellationToken ct)
        {
            if (_disposed) return;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_disposed && Pipe.IsConnected)
                    await FrameWriter.WriteAsync(Pipe, message, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (Pipe.IsConnected) Pipe.Disconnect();
            }
            catch
            {
                // Already gone.
            }

            Pipe.Dispose();
            _writeLock.Dispose();
        }
    }
}

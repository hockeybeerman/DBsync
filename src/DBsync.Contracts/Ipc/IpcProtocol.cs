using System.Text.Json;
using System.Text.Json.Serialization;

namespace DBsync.Contracts.Ipc;

/// <summary>Wire constants shared by the service's pipe server and every client.</summary>
public static class IpcProtocol
{
    /// <summary>
    /// Named pipe the service listens on. Clients connect to <c>\\.\pipe\DBsync.v1</c>;
    /// the pipe ACL grants Authenticated Users read/write so any interactive session's
    /// tray app can attach.
    /// </summary>
    public const string PipeName = "DBsync.v1";

    /// <summary>Bumped whenever a message shape changes incompatibly.</summary>
    public const int Version = 1;

    /// <summary>Frames are newline-delimited UTF-8 JSON; a lone '\n' terminates each message.</summary>
    public const byte FrameTerminator = (byte)'\n';

    /// <summary>Refuse frames larger than this so a wedged peer cannot exhaust memory.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

public enum IpcMessageType
{
    Request,
    Response,
    Event,
}

public enum IpcRequestKind
{
    Ping,
    GetState,
    AddPair,
    UpdatePair,
    DeletePair,
    SetPairEnabled,
    PauseAll,
    ResumeAll,
    SyncNow,
    GetActivity,
    GetActivitySummary,
    GetConflicts,
    ResolveConflict,
    ProbeDestination,
    StoreCredentials,
    Subscribe,
}

public enum IpcEventKind
{
    /// <summary>Anything structural changed (pair added/removed, global pause). Repaint everything.</summary>
    StateChanged,

    /// <summary>A single pair's status, percent, or detail line moved.</summary>
    PairChanged,

    /// <summary>A conflict was raised and parked.</summary>
    ConflictRaised,

    /// <summary>A destination went offline or came back.</summary>
    ReachabilityChanged,

    /// <summary>A row was appended to the activity log.</summary>
    LogAppended,
}

/// <summary>The single envelope every frame uses, in both directions.</summary>
public sealed class IpcMessage
{
    /// <summary>Correlates a response with its request. Absent on events.</summary>
    public string? Id { get; set; }

    public IpcMessageType Type { get; set; }

    /// <summary>Set on requests and their responses.</summary>
    public IpcRequestKind? Request { get; set; }

    /// <summary>Set on events.</summary>
    public IpcEventKind? Event { get; set; }

    /// <summary>Request arguments or response/event body, shaped by <see cref="Request"/>/<see cref="Event"/>.</summary>
    public JsonElement? Payload { get; set; }

    /// <summary>Non-null on a response means the request failed and <see cref="Payload"/> is empty.</summary>
    public string? Error { get; set; }

    public static IpcMessage MakeRequest(IpcRequestKind kind, object? payload = null) => new()
    {
        Id = Guid.NewGuid().ToString("n"),
        Type = IpcMessageType.Request,
        Request = kind,
        Payload = Wrap(payload),
    };

    public static IpcMessage MakeResponse(IpcMessage request, object? payload) => new()
    {
        Id = request.Id,
        Type = IpcMessageType.Response,
        Request = request.Request,
        Payload = Wrap(payload),
    };

    public static IpcMessage MakeError(IpcMessage request, string error) => new()
    {
        Id = request.Id,
        Type = IpcMessageType.Response,
        Request = request.Request,
        Error = error,
    };

    public static IpcMessage MakeEvent(IpcEventKind kind, object? payload) => new()
    {
        Type = IpcMessageType.Event,
        Event = kind,
        Payload = Wrap(payload),
    };

    /// <summary>Deserialises <see cref="Payload"/> into <typeparamref name="T"/>, or returns a default instance.</summary>
    public T PayloadAs<T>() where T : new() =>
        Payload is null or { ValueKind: JsonValueKind.Null }
            ? new T()
            : Payload.Value.Deserialize<T>(IpcProtocol.Json) ?? new T();

    private static JsonElement? Wrap(object? payload) =>
        payload is null
            ? null
            : JsonSerializer.SerializeToElement(payload, payload.GetType(), IpcProtocol.Json);
}

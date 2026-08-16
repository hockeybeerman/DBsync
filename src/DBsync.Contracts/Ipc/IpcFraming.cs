using System.Text;
using System.Text.Json;

namespace DBsync.Contracts.Ipc;

/// <summary>
/// Reads newline-delimited JSON frames off a pipe. One instance per stream — it owns the
/// carry-over buffer between reads, so frames that straddle a read boundary survive.
/// </summary>
public sealed class FrameReader
{
    private readonly Stream _stream;
    private readonly byte[] _read = new byte[16 * 1024];
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingLength;

    public FrameReader(Stream stream) => _stream = stream;

    /// <summary>Returns the next message, or null when the peer closed the pipe.</summary>
    public async Task<IpcMessage?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var terminator = Array.IndexOf(_pending, IpcProtocol.FrameTerminator, 0, _pendingLength);
            if (terminator >= 0)
            {
                var message = Decode(_pending, terminator);

                var consumed = terminator + 1;
                Buffer.BlockCopy(_pending, consumed, _pending, 0, _pendingLength - consumed);
                _pendingLength -= consumed;

                // An empty frame is a keepalive; keep draining rather than surfacing a null read,
                // which callers treat as a disconnect.
                if (message is not null) return message;
                continue;
            }

            if (_pendingLength > IpcProtocol.MaxFrameBytes)
                throw new InvalidDataException($"IPC frame exceeded {IpcProtocol.MaxFrameBytes} bytes.");

            var count = await _stream.ReadAsync(_read, ct).ConfigureAwait(false);
            if (count == 0) return null;

            EnsureCapacity(_pendingLength + count);
            Buffer.BlockCopy(_read, 0, _pending, _pendingLength, count);
            _pendingLength += count;
        }
    }

    /// <summary>Kept out of the async method — a span local cannot cross an await boundary.</summary>
    private static IpcMessage? Decode(byte[] buffer, int length) =>
        length == 0
            ? null
            : JsonSerializer.Deserialize<IpcMessage>(new ReadOnlySpan<byte>(buffer, 0, length), IpcProtocol.Json);

    private void EnsureCapacity(int needed)
    {
        if (_pending.Length >= needed) return;
        var size = Math.Max(needed, Math.Max(_pending.Length * 2, 16 * 1024));
        Array.Resize(ref _pending, size);
    }
}

/// <summary>Serialises a message and writes it as one frame. Callers must serialise their own writes.</summary>
public static class FrameWriter
{
    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, IpcProtocol.Json);
        if (bytes.Length + 1 > IpcProtocol.MaxFrameBytes)
            throw new InvalidDataException("IPC frame too large to send.");

        var buffer = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, buffer, 0, bytes.Length);
        buffer[^1] = IpcProtocol.FrameTerminator;

        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>UTF-8 text of a message, for logging and the CLI's --raw mode.</summary>
    public static string Describe(IpcMessage message) =>
        Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message, IpcProtocol.Json));
}

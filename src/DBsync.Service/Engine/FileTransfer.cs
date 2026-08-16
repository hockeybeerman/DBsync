using DBsync.Service.Configuration;
using DBsync.Service.Win32;
using Microsoft.Extensions.Logging;

namespace DBsync.Service.Engine;

public enum TransferStatus
{
    Copied,

    /// <summary>Source was held open by another process and shadow copy was unavailable or disabled.</summary>
    Locked,

    Failed,
}

public sealed record TransferResult(TransferStatus Status, long Bytes, string? Message)
{
    public static TransferResult Ok(long bytes) => new(TransferStatus.Copied, bytes, null);
    public static TransferResult Locked(string message) => new(TransferStatus.Locked, 0, message);
    public static TransferResult Failed(string message) => new(TransferStatus.Failed, 0, message);
}

/// <summary>
/// Copies one file between the two roots. Writes land in a <c>.dbsync-part</c> temp beside the
/// destination and are renamed into place, so an interrupted transfer never leaves a truncated
/// file where the other side will pick it up as authoritative.
/// </summary>
public sealed class FileTransfer
{
    private const int BufferSize = 1024 * 1024;

    private static readonly int[] RetryDelaysMs = { 250, 1000, 3000 };

    private readonly ILogger _log;

    public FileTransfer(ILogger log) => _log = log;

    /// <param name="throttle">Applied only when writing toward the share; inbound copies run free.</param>
    /// <param name="useVss">Retry a locked source through a volume shadow copy.</param>
    /// <param name="versionsKept">Superseded destination copies to retain. 0 disables versioning.</param>
    /// <param name="destinationRoot">Root the version store hangs off.</param>
    public async Task<TransferResult> CopyAsync(
        string source,
        string destination,
        string destinationRoot,
        RateLimiter? throttle,
        bool useVss,
        int versionsKept,
        int attempts,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        Exception? lastFailure = null;

        for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await CopyOnceAsync(source, destination, destinationRoot, throttle,
                    versionsKept, progress, ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                lastFailure = ex;

                // The source is open elsewhere. A shadow copy reads the on-disk state without
                // waiting for the writer to let go; without it, back off and try again.
                if (useVss)
                {
                    var shadowed = await TryShadowCopyAsync(source, destination, destinationRoot,
                        throttle, versionsKept, progress, ct).ConfigureAwait(false);
                    if (shadowed is not null) return shadowed;
                }

                await DelayAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                lastFailure = ex;
                await DelayAsync(attempt, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                return TransferResult.Failed(ex.Message);
            }
        }

        if (lastFailure is IOException io && IsSharingViolation(io))
            return TransferResult.Locked("File is in use by another process.");

        return TransferResult.Failed(lastFailure?.Message ?? "Copy failed.");
    }

    private async Task<TransferResult> CopyOnceAsync(
        string source,
        string destination,
        string destinationRoot,
        RateLimiter? throttle,
        int versionsKept,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = destination + ServicePaths.TempSuffix;
        var copied = 0L;

        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                             FileShare.Read | FileShare.Delete, BufferSize, useAsync: true))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write,
                             FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;

                    if (throttle is { Enabled: true })
                        await throttle.ConsumeAsync(read, ct).ConfigureAwait(false);

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(copied);
                }
            }

            // Carry the timestamp across so the baseline comparison on the far side matches and
            // the file is not immediately seen as "changed" and sent back.
            var written = File.GetLastWriteTimeUtc(source);
            File.SetLastWriteTimeUtc(temp, written);

            if (versionsKept > 0 && File.Exists(destination))
                Retain(destination, destinationRoot, versionsKept);

            File.Move(temp, destination, overwrite: true);
            return TransferResult.Ok(copied);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async Task<TransferResult?> TryShadowCopyAsync(
        string source,
        string destination,
        string destinationRoot,
        RateLimiter? throttle,
        int versionsKept,
        IProgress<long>? progress,
        CancellationToken ct)
    {
        using var snapshot = ShadowCopySession.TryCreate(source);
        if (snapshot is null)
        {
            _log.LogDebug("Shadow copy unavailable for {Source}; falling back to retry.", source);
            return null;
        }

        try
        {
            var shadowSource = snapshot.MapPath(source);
            _log.LogDebug("Reading locked {Source} through {Shadow}.", source, shadowSource);
            return await CopyOnceAsync(shadowSource, destination, destinationRoot, throttle,
                versionsKept, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Shadow-copy read of {Source} failed.", source);
            return null;
        }
    }

    /// <summary>
    /// Moves the copy about to be overwritten into the destination root's version store as
    /// <c>.dbsync-versions\&lt;relative dir&gt;\&lt;yyyyMMdd-HHmmss&gt;__&lt;name&gt;</c>, then prunes to
    /// <paramref name="versionsKept"/>.
    /// </summary>
    private void Retain(string destination, string destinationRoot, int versionsKept)
    {
        try
        {
            var relative = Path.GetRelativePath(destinationRoot, destination);
            var relativeDirectory = Path.GetDirectoryName(relative) ?? "";
            var store = Path.Combine(destinationRoot, ServicePaths.VersionFolderName, relativeDirectory);
            Directory.CreateDirectory(store);

            var name = Path.GetFileName(destination);
            var stamp = File.GetLastWriteTimeUtc(destination).ToString("yyyyMMdd-HHmmss");
            File.Move(destination, Path.Combine(store, $"{stamp}__{name}"), overwrite: true);

            var existing = Directory.GetFiles(store, "*__" + name)
                .OrderByDescending(path => path)
                .Skip(versionsKept)
                .ToList();

            foreach (var stale in existing) TryDelete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Versioning is a convenience; never let it block the sync itself.
            _log.LogDebug(ex, "Could not retain a version of {Destination}.", destination);
        }
    }

    /// <summary>Deletes a file, moving it to the version store first when versioning is on.</summary>
    public bool Remove(string path, string root, int versionsKept, out string? message)
    {
        message = null;
        try
        {
            if (!File.Exists(path)) return true;

            if (versionsKept > 0) Retain(path, root, versionsKept);
            if (File.Exists(path)) File.Delete(path);

            PruneEmptyDirectories(Path.GetDirectoryName(path), root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = ex.Message;
            return false;
        }
    }

    /// <summary>Removes directories left empty by a delete, stopping at the pair root.</summary>
    private static void PruneEmptyDirectories(string? directory, string root)
    {
        var stop = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        while (!string.IsNullOrEmpty(directory))
        {
            var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(stop, StringComparison.OrdinalIgnoreCase)) return;
            if (!full.StartsWith(stop, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(full)) return;
            if (Directory.EnumerateFileSystemEntries(full).Any()) return;

            try { Directory.Delete(full); } catch { return; }
            directory = Path.GetDirectoryName(full);
        }
    }

    private static async Task DelayAsync(int attempt, CancellationToken ct)
    {
        var delay = RetryDelaysMs[Math.Min(attempt, RetryDelaysMs.Length - 1)];
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stranded temp is swept by the next successful copy to the same destination.
        }
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33) in an IOException's HRESULT.</summary>
    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}

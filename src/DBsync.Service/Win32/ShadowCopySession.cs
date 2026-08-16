using System.Management;

namespace DBsync.Service.Win32;

/// <summary>
/// A point-in-time volume snapshot, used to read files another process holds open. Created
/// through <c>Win32_ShadowCopy</c> so the service does not have to host the VSS COM writer
/// stack; the snapshot is deleted on dispose.
/// </summary>
public sealed class ShadowCopySession : IDisposable
{
    private readonly string _volumeRoot;
    private readonly string _deviceObject;
    private readonly string _shadowId;
    private bool _disposed;

    private ShadowCopySession(string volumeRoot, string deviceObject, string shadowId)
    {
        _volumeRoot = volumeRoot;
        _deviceObject = deviceObject;
        _shadowId = shadowId;
    }

    /// <summary>
    /// Snapshots the volume holding <paramref name="path"/>. Returns null when the volume cannot
    /// be snapshotted (network paths, unsupported filesystems, VSS disabled) — callers fall back
    /// to logging the file as skipped.
    /// </summary>
    public static ShadowCopySession? TryCreate(string path)
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(volumeRoot) || volumeRoot.StartsWith(@"\\", StringComparison.Ordinal))
            return null; // Only local volumes can be snapshotted from this machine.

        try
        {
            using var shadowClass = new ManagementClass("Win32_ShadowCopy");
            using var parameters = shadowClass.GetMethodParameters("Create");
            parameters["Volume"] = volumeRoot;
            parameters["Context"] = "ClientAccessible";

            using var result = shadowClass.InvokeMethod("Create", parameters, null);
            if (result is null || Convert.ToUInt32(result["ReturnValue"]) != 0) return null;

            var shadowId = result["ShadowID"] as string;
            if (string.IsNullOrEmpty(shadowId)) return null;

            var deviceObject = LookupDeviceObject(shadowId);
            if (deviceObject is null)
            {
                Delete(shadowId);
                return null;
            }

            return new ShadowCopySession(volumeRoot, deviceObject, shadowId);
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Rewrites a path on the snapshotted volume to its address inside the snapshot, e.g.
    /// <c>C:\Work\ledger.xlsx</c> → <c>\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy7\Work\ledger.xlsx</c>.
    /// </summary>
    public string MapPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        if (!root.Equals(_volumeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{path} is not on the snapshotted volume {_volumeRoot}.");

        var relative = full.Substring(root.Length).TrimStart('\\');
        return Path.Combine(_deviceObject + @"\", relative);
    }

    private static string? LookupDeviceObject(string shadowId)
    {
        var query = new ObjectQuery($"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID='{shadowId}'");
        using var searcher = new ManagementObjectSearcher(query);
        using var results = searcher.Get();

        foreach (var item in results)
        {
            using var row = (ManagementObject)item;
            var device = row["DeviceObject"] as string;
            if (!string.IsNullOrEmpty(device)) return device;
        }

        return null;
    }

    private static void Delete(string shadowId)
    {
        try
        {
            using var shadow = new ManagementObject($"Win32_ShadowCopy.ID='{shadowId}'");
            shadow.Delete();
        }
        catch
        {
            // A leaked snapshot is reclaimed by the volume's shadow storage limit; never fail a
            // sync over cleanup.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Delete(_shadowId);
    }
}

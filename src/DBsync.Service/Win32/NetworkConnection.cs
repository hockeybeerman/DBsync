using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DBsync.Service.Win32;

/// <summary>
/// Establishes an authenticated session to a UNC share for the life of the object. The service
/// runs as LocalSystem, which has no network identity of its own, so any share that needs
/// credentials needs one of these wrapped around the access.
/// </summary>
public sealed class NetworkConnection : IDisposable
{
    private const int ResourceTypeDisk = 1;
    private const int ConnectTemporary = 0x00000004;

    private const int NoError = 0;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorAlreadyAssigned = 85;

    private readonly string? _root;
    private bool _connected;

    private NetworkConnection(string? root, bool connected)
    {
        _root = root;
        _connected = connected;
    }

    /// <summary>
    /// Connects to the server hosting <paramref name="path"/>. Returns a no-op handle when the
    /// path is not a UNC path, when no credentials are configured, or when a usable session
    /// already exists — callers can always wrap access in a <c>using</c>.
    /// </summary>
    public static NetworkConnection Attach(string path, NetworkCredentialRecord? credentials)
    {
        if (credentials is null || string.IsNullOrEmpty(credentials.Username)) return new NetworkConnection(null, false);

        var root = UncRoot(path);
        if (root is null) return new NetworkConnection(null, false);

        var resource = new NETRESOURCE
        {
            dwType = ResourceTypeDisk,
            lpRemoteName = root,
        };

        var result = WNetAddConnection2(ref resource, credentials.Password, credentials.Username, ConnectTemporary);

        // A conflicting session means the OS already holds a mapping to this server under a
        // different identity. Tear ours down rather than fighting it, and let the access attempt
        // report the real permission error.
        if (result == ErrorSessionCredentialConflict || result == ErrorAlreadyAssigned)
            return new NetworkConnection(null, false);

        if (result != NoError)
            throw new Win32Exception(result, $"Could not connect to {root}: {new Win32Exception(result).Message}");

        return new NetworkConnection(root, true);
    }

    /// <summary>Returns <c>\\server\share</c> for a UNC path, or null for a local/mapped path.</summary>
    public static string? UncRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\", StringComparison.Ordinal)) return null;

        var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : @"\\" + parts[0] + @"\" + parts[1];
    }

    public void Dispose()
    {
        if (!_connected || _root is null) return;
        _connected = false;
        WNetCancelConnection2(_root, 0, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    private static extern int WNetAddConnection2(ref NETRESOURCE resource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);
}

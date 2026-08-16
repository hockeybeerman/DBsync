using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DBsync.Service.Win32;

/// <summary>Free-space and ownership lookups that <see cref="System.IO.DriveInfo"/> cannot do over UNC.</summary>
public static class VolumeInfo
{
    /// <summary>
    /// Free bytes available to the calling identity at <paramref name="path"/>. Works for UNC
    /// paths, where DriveInfo does not. Returns 0 when the query fails.
    /// </summary>
    public static long FreeBytes(string path)
    {
        var probe = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        return GetDiskFreeSpaceEx(probe, out var free, out _, out _) ? (long)free : 0L;
    }

    /// <summary>
    /// Owner account name for a file, without the domain prefix — the "dana" / "m.reyes" shown
    /// under each card in the conflict dialog. Falls back to an empty string.
    /// </summary>
    public static string OwnerOf(string path)
    {
        try
        {
            var security = new FileInfo(path).GetAccessControl();
            var owner = security.GetOwner(typeof(NTAccount))?.Value;
            if (string.IsNullOrEmpty(owner)) return "";

            var slash = owner.LastIndexOf('\\');
            return slash >= 0 ? owner.Substring(slash + 1) : owner;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IdentityNotMappedException
                                      or IOException or PlatformNotSupportedException)
        {
            return "";
        }
    }

    /// <summary>Renders a byte count the way the wizard's validation line does — "2.1 TB free".</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = { "bytes", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit switch
        {
            0 => $"{bytes} bytes",
            1 or 2 => $"{value:0} {units[unit]}",
            _ => $"{value:0.#} {units[unit]}",
        };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDiskFreeSpaceExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}

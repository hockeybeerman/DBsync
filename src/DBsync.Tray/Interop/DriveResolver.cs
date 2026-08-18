using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DBsync.Tray.Interop;

/// <summary>
/// Resolves a mapped drive letter to the UNC path behind it.
/// <para>
/// The wizard offers "Mapped drive" as a destination, but drive letters are per-session: they are
/// created by the interactive user's logon and do not exist in the service's session, which runs
/// as LocalSystem. A pair saved as <c>Z:\team\projects</c> would resolve against an empty drive
/// namespace and sit in Waiting forever, reporting a share that is actually fine as unreachable.
/// So the letter is resolved here, on the user's side, and the UNC path is what gets saved.
/// </para>
/// </summary>
public static class DriveResolver
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorNotConnected = 2250;

    public sealed record Resolution(bool Success, string Path, string? Error);

    /// <summary>
    /// Rewrites <paramref name="path"/> to its UNC equivalent when it sits on a mapped network
    /// drive. A path that is already UNC is returned unchanged; a local disk or <c>subst</c> drive
    /// fails, because there is nothing the service could reach.
    /// </summary>
    public static Resolution ToUnc(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new Resolution(false, path, "Enter a destination path.");

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return new Resolution(true, trimmed, null);

        var root = Path.GetPathRoot(trimmed);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            return new Resolution(false, trimmed, $"'{trimmed}' is not a drive path.");

        var letter = root[..2];
        var remote = new StringBuilder(512);
        var length = remote.Capacity;
        var result = WNetGetConnection(letter, remote, ref length);

        if (result == ErrorMoreData)
        {
            remote = new StringBuilder(length);
            result = WNetGetConnection(letter, remote, ref length);
        }

        if (result == ErrorNotConnected)
        {
            return new Resolution(false, trimmed,
                $"{letter} is not a network drive. The DBsync service runs as LocalSystem and " +
                "cannot see local or substituted drives — choose a UNC path instead.");
        }

        if (result != NoError)
        {
            return new Resolution(false, trimmed,
                $"Could not resolve {letter} — {new System.ComponentModel.Win32Exception(result).Message}");
        }

        var remainder = trimmed[root.Length..].TrimStart(Path.DirectorySeparatorChar);
        var target = remote.ToString().TrimEnd(Path.DirectorySeparatorChar);
        var resolved = remainder.Length == 0 ? target : Path.Combine(target, remainder);

        return new Resolution(true, resolved, null);
    }

    /// <summary>Drive letters currently mapped to a network share, for the wizard's hints.</summary>
    public static IEnumerable<(string Letter, string Target)> MappedDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Network) continue;

            var resolution = ToUnc(drive.Name);
            if (resolution.Success) yield return (drive.Name.TrimEnd('\\'), resolution.Path ?? "");
        }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}

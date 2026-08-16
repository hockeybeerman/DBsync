using System.Runtime.InteropServices;
using System.Text;

namespace DBsync.Service.Win32;

/// <summary>Username/password pair read back out of Windows Credential Manager.</summary>
public sealed record NetworkCredentialRecord(string Username, string Password);

/// <summary>
/// Thin wrapper over the Win32 credential store. UNC credentials never touch config.json —
/// they live here, in the service account's credential set, and are read on demand when a
/// share needs authenticating.
/// </summary>
public static class CredentialManager
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    /// <summary>Target name used for a pair's share credentials.</summary>
    public static string TargetFor(string pairId) => "DBsync:" + pairId;

    public static void Write(string pairId, string username, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password ?? "");
        var blobHandle = Marshal.AllocCoTaskMem(blob.Length);
        var target = Marshal.StringToCoTaskMemUni(TargetFor(pairId));
        var user = Marshal.StringToCoTaskMemUni(username ?? "");
        var comment = Marshal.StringToCoTaskMemUni("DBsync share credentials");

        try
        {
            Marshal.Copy(blob, 0, blobHandle, blob.Length);

            var credential = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                Comment = comment,
                CredentialBlob = blobHandle,
                CredentialBlobSize = blob.Length,
                Persist = CredPersistLocalMachine,
            };

            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException(
                    "CredWrite failed: " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally
        {
            Marshal.FreeCoTaskMem(blobHandle);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(comment);
            Array.Clear(blob, 0, blob.Length);
        }
    }

    public static NetworkCredentialRecord? Read(string pairId)
    {
        if (!CredRead(TargetFor(pairId), CredTypeGeneric, 0, out var handle)) return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(handle);
            var username = credential.UserName == IntPtr.Zero
                ? ""
                : Marshal.PtrToStringUni(credential.UserName) ?? "";
            var password = credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);

            return new NetworkCredentialRecord(username, password);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static void Delete(string pairId) => CredDelete(TargetFor(pairId), CredTypeGeneric, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

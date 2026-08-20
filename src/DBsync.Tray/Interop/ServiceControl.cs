using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DBsync.Tray.Interop;

/// <summary>What the SCM says about the DBsync service right now.</summary>
public enum ServiceInstallState
{
    /// <summary>No such service is registered — it was never installed, or has been removed.</summary>
    NotInstalled,

    Stopped,

    /// <summary>Mid-transition: starting, stopping, or paused.</summary>
    Changing,

    Running,

    /// <summary>The SCM could not be asked. Treated as unknown rather than assumed bad.</summary>
    Unknown,
}

/// <summary>
/// Reads and starts the DBsync Windows service.
/// <para>
/// Queried directly through the SCM rather than by adding a dependency: a status read needs only
/// <c>SC_MANAGER_CONNECT</c> and <c>SERVICE_QUERY_STATUS</c>, which an ordinary user already has.
/// Starting is a different matter and needs elevation, so that path shells out and lets Windows
/// raise the consent prompt.
/// </para>
/// </summary>
public static class ServiceControl
{
    public const string ServiceName = "DBsync";

    private const int ScManagerConnect = 0x0001;
    private const int ServiceQueryStatus = 0x0004;
    private const int ErrorServiceDoesNotExist = 1060;

    public static ServiceInstallState Query()
    {
        var manager = IntPtr.Zero;
        var service = IntPtr.Zero;

        try
        {
            manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero) return ServiceInstallState.Unknown;

            service = OpenService(manager, ServiceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                    ? ServiceInstallState.NotInstalled
                    : ServiceInstallState.Unknown;
            }

            if (!QueryServiceStatus(service, out var status)) return ServiceInstallState.Unknown;

            return status.dwCurrentState switch
            {
                ServiceRunning => ServiceInstallState.Running,
                ServiceStopped => ServiceInstallState.Stopped,
                _ => ServiceInstallState.Changing,
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ServiceInstallState.Unknown;
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            if (manager != IntPtr.Zero) CloseServiceHandle(manager);
        }
    }

    /// <summary>
    /// Asks Windows to start the service, elevating first.
    /// <para>
    /// Starting a service is an administrator action, and the tray app runs as an ordinary user —
    /// so this launches <c>sc.exe</c> with the runas verb rather than failing with an access
    /// denied the user cannot act on. Returns false if they decline the consent prompt.
    /// </para>
    /// </summary>
    public static bool TryStartElevated(out string? error)
    {
        error = null;

        try
        {
            var process = Process.Start(new ProcessStartInfo("sc.exe", $"start {ServiceName}")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                error = "Windows would not run sc.exe.";
                return false;
            }

            process.WaitForExit(15_000);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the consent prompt was declined. Not a failure worth shouting about.
            error = null;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private const int ServiceStopped = 1;
    private const int ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

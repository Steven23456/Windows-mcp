using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Shutdown;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class PowerService : IPowerService
{
    public Task ExecuteAsync(string action, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        switch (action.ToLowerInvariant())
        {
            case "shutdown":
                EnableShutdownPrivilege();
                if (!PInvoke.ExitWindowsEx(EXIT_WINDOWS_FLAGS.EWX_SHUTDOWN, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Shutdown failed");
                break;
            case "reboot":
                EnableShutdownPrivilege();
                if (!PInvoke.ExitWindowsEx(EXIT_WINDOWS_FLAGS.EWX_REBOOT, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Reboot failed");
                break;
            case "logoff":
                // Logoff needs no special privilege, but still verify the call succeeded.
                if (!PInvoke.ExitWindowsEx(EXIT_WINDOWS_FLAGS.EWX_LOGOFF, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Logoff failed");
                break;
            case "lock":
                if (!PInvoke.LockWorkStation())
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "LockWorkStation failed");
                break;
            case "sleep":
                if (!PInvoke.SetSuspendState(false, false, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Sleep failed (suspend may be disabled)");
                break;
            case "hibernate":
                if (!PInvoke.SetSuspendState(true, false, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Hibernate failed (hibernation may be disabled)");
                break;
            default:
                throw new ArgumentException($"Unknown power action '{action}'; expected shutdown|reboot|logoff|lock|sleep|hibernate");
        }

        return Task.CompletedTask;
    }

    // ExitWindowsEx(SHUTDOWN|REBOOT) silently no-ops unless SeShutdownPrivilege is ENABLED in the
    // process token. The interactive user holds the privilege; it just needs enabling each run.
    private static unsafe void EnableShutdownPrivilege()
    {
        HANDLE token;
        if (!PInvoke.OpenProcessToken(
                PInvoke.GetCurrentProcess(),
                TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY,
                &token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed");

        try
        {
            if (!PInvoke.LookupPrivilegeValue(null, "SeShutdownPrivilege", out LUID luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed");

            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            tp.Privileges[0] = new LUID_AND_ATTRIBUTES
            {
                Luid = luid,
                Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED
            };

            if (!PInvoke.AdjustTokenPrivileges(token, false, &tp, 0, null, null))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed");

            // AdjustTokenPrivileges returns true even when the privilege was not assigned;
            // ERROR_NOT_ALL_ASSIGNED (1300) means the token does not hold SeShutdownPrivilege.
            const int ERROR_NOT_ALL_ASSIGNED = 1300;
            if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                throw new InvalidOperationException(
                    "SeShutdownPrivilege is not held by this process token; cannot shut down or reboot.");
        }
        finally
        {
            PInvoke.CloseHandle(token);
        }
    }
}

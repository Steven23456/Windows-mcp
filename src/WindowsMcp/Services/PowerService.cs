using Windows.Win32;
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
                PInvoke.ExitWindowsEx(EXIT_WINDOWS_FLAGS.EWX_SHUTDOWN, 0);
                break;
            case "reboot":
                PInvoke.ExitWindowsEx(EXIT_WINDOWS_FLAGS.EWX_REBOOT, 0);
                break;
            case "logoff":
                PInvoke.ExitWindowsEx(EXIT_WINDOWS_FLAGS.EWX_LOGOFF, 0);
                break;
            case "lock":
                PInvoke.LockWorkStation();
                break;
            case "sleep":
                PInvoke.SetSuspendState(false, false, false);
                break;
            case "hibernate":
                PInvoke.SetSuspendState(true, false, false);
                break;
            default:
                throw new ArgumentException($"Unknown power action '{action}'; expected shutdown|reboot|logoff|lock|sleep|hibernate");
        }

        return Task.CompletedTask;
    }
}

using System.Security.Principal;

namespace WindowsMcp.Services;

/// <summary>
/// E-8: the one elevation check - moved here from StartupReportService's private IsElevated so
/// startup_report and the session probe share it.
/// </summary>
internal static class ProcessElevation
{
    /// <summary>Whether the server process's token is in the Administrators role; false when it cannot be read.</summary>
    internal static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

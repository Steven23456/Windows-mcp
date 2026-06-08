using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// Builds an aggregated boot/persistence report (processes, Run keys, Startup folders,
/// scheduled tasks, auto-start services, hosts file, Winsock LSP, shell extensions).
/// </summary>
public interface IStartupReportService
{
    /// <param name="includeProcesses">
    /// Include the full running-process inventory. Off by default: it is the largest and least
    /// persistence-relevant section (transient state), and dominates the report size.
    /// </param>
    Task<StartupReportDto> BuildAsync(bool includeProcesses = false, CancellationToken ct = default);
}

using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// Builds an aggregated boot/persistence report (processes, Run keys, Startup folders,
/// scheduled tasks, auto-start services, hosts file, Winsock LSP, shell extensions).
/// </summary>
public interface IStartupReportService
{
    Task<StartupReportDto> BuildAsync(CancellationToken ct = default);
}

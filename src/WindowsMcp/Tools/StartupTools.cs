using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Startup;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class StartupTools
{
    private readonly IStartupReportService _report;

    public StartupTools(IStartupReportService report) => _report = report;

    [McpServerTool, Description(
        "Generate a HiJackThis-style boot/persistence report: running processes, Run/RunOnce " +
        "keys (with effective enabled/disabled state), Startup folders, startup-relevant " +
        "scheduled tasks (logon/boot or missing-target), auto-start services, hosts file, " +
        "Winsock LSP providers, and shell extensions. Every file-backed entry carries a " +
        "catalog-aware code-signing trust flag (Microsoft catalog-signed components show as " +
        "trusted). Read-only. Returns JSON followed by a readable text rendering.")]
    public async Task<string> StartupReport(CancellationToken ct = default)
    {
        var dto = await _report.BuildAsync(ct);
        return JsonSerializer.Serialize(dto) + "\n\n" + StartupReportRenderer.Render(dto);
    }
}

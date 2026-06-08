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
        "Generate a HiJackThis-style boot/persistence report (read-only): Run/RunOnce keys " +
        "(all hives incl. per-user SIDs, with effective enabled state), Startup folders, " +
        "startup-relevant scheduled tasks, auto-start services, hosts file, DNS servers, " +
        "Winsock LSP providers, shell extensions, Control Panel applets, accessibility-AT " +
        "hooks, Image File Execution Options, Winlogon hooks, AppInit_DLLs, Active Setup, " +
        "browser proxy and trusted-zone sites. Every file-backed entry carries a catalog-aware " +
        "code-signing trust flag. Processes are excluded unless includeProcesses=true (they " +
        "dominate size). format: json (default) | text | both.")]
    public async Task<string> StartupReport(
        [Description("Include the full running-process inventory (large). Default false.")] bool includeProcesses = false,
        [Description("Output format: json | text | both. Default json.")] string format = "json",
        CancellationToken ct = default)
    {
        var dto = await _report.BuildAsync(includeProcesses, ct);

        return format.ToLowerInvariant() switch
        {
            "text" => StartupReportRenderer.Render(dto),
            "both" => JsonSerializer.Serialize(dto) + "\n\n" + StartupReportRenderer.Render(dto),
            "json" => JsonSerializer.Serialize(dto),
            _ => throw new ArgumentException($"Unknown format '{format}'; expected json|text|both"),
        };
    }
}

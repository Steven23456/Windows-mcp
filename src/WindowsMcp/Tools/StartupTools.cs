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

    [McpServerTool(Title = "Startup report", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Generate a HiJackThis-style boot/persistence report (read-only): Run/RunOnce keys " +
        "(all hives incl. per-user SIDs, with effective enabled state), Startup folders, " +
        "startup-relevant scheduled tasks, auto-start services, hosts file, DNS servers, " +
        "Winsock LSP providers, shell extensions, Control Panel applets, accessibility-AT " +
        "hooks, Image File Execution Options, Winlogon hooks, AppInit_DLLs, Active Setup, " +
        "browser proxy and trusted-zone sites. Every file-backed entry carries a catalog-aware " +
        "code-signing trust flag. Processes are excluded unless includeProcesses=true (they " +
        "dominate size). format: summary (default — section counts + only flagged/untrusted/" +
        "missing-target entries, fits inline) | json (full structured) | text (full rendering) | both.")]
    public async Task<string> StartupReport(
        [Description("Include the full running-process inventory (large). Default false.")] bool includeProcesses = false,
        [Description("Output format: summary | json | text | both. Default summary.")] string format = "summary",
        CancellationToken ct = default)
    {
        var dto = await _report.BuildAsync(includeProcesses, ct);

        return format.ToLowerInvariant() switch
        {
            "summary" => StartupReportRenderer.RenderSummary(dto),
            "text" => StartupReportRenderer.Render(dto),
            "both" => JsonSerializer.Serialize(dto) + "\n\n" + StartupReportRenderer.Render(dto),
            "json" => JsonSerializer.Serialize(dto),
            _ => throw new ArgumentException($"Unknown format '{format}'; expected summary|json|text|both"),
        };
    }
}

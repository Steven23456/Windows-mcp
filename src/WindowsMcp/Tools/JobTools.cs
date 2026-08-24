using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class JobTools
{
    private readonly IJobService _jobs;

    public JobTools(IJobService jobs) => _jobs = jobs;

    [McpServerTool, Description(
        "Manage background PowerShell jobs started via powershell background:true. " +
        "mode: status (one job's state/pid/exit code), output (buffered stdout/stderr; tail limits " +
        "to the last N chars per stream), cancel (kill a running job's process tree), " +
        "list (all retained jobs). " +
        "id: job id like 'j1' (status/output/cancel). " +
        "Jobs run concurrently outside the foreground PowerShell gate: max 8 running (new starts " +
        "are rejected when full), 60-min per-job backstop (state becomes timedOut), output bounded " +
        "at ~1 MB per stream (oldest chars trimmed; Trimmed counters report how much), and the ~32 " +
        "most recent finished jobs are retained before eviction. Unknown ids return found:false / " +
        "cancelled:false rather than erroring.")]
    public string Job(
        [Description("Mode: status, output, cancel, list")] string mode,
        [Description("Job id (status/output/cancel modes)")] string? id = null,
        [Description("Output mode: return only the last N chars per stream (0 = all buffered)")] int tail = 0)
    {
        switch (mode.ToLowerInvariant())
        {
            case "status":
                if (string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("status mode requires 'id'");
                return JsonSerializer.Serialize<object>(_jobs.GetStatus(id) ?? (object)new { found = false, id });
            case "output":
                if (string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("output mode requires 'id'");
                return JsonSerializer.Serialize<object>(_jobs.GetOutput(id, tail) ?? (object)new { found = false, id });
            case "cancel":
                if (string.IsNullOrWhiteSpace(id))
                    throw new ArgumentException("cancel mode requires 'id'");
                return JsonSerializer.Serialize(new { cancelled = _jobs.Cancel(id) });
            case "list":
                return JsonSerializer.Serialize(_jobs.List());
            default:
                throw new ArgumentException($"Unknown mode '{mode}'; expected status|output|cancel|list");
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ShellTools
{
    private readonly IPowerShellService _ps;
    private readonly IJobService _jobs;
    private readonly TimeSpan _heartbeatInterval;

    public ShellTools(IPowerShellService ps, IJobService jobs)
        : this(ps, jobs, TimeSpan.FromSeconds(10)) { }

    // Test ctor: a short heartbeat interval keeps the heartbeat test from sleeping 10s.
    internal ShellTools(IPowerShellService ps, IJobService jobs, TimeSpan heartbeatInterval)
    {
        _ps = ps;
        _jobs = jobs;
        _heartbeatInterval = heartbeatInterval;
    }

    [McpServerTool, Description(
        "Execute a PowerShell command and return the result including stdout, stderr, and exit " +
        "code. Long-running foreground calls emit MCP progress heartbeats every 10s so " +
        "spec-compliant clients reset their request timeout (foreground execution backstop: 15 " +
        "min). For commands longer than that — installers, DISM, bulk hashes — pass " +
        "background:true to run it as a job instead: returns {Id, Pid, State} immediately, then " +
        "poll with the 'job' tool (status|output|cancel|list).")]
    public async Task<string> Powershell(
        [Description("PowerShell command or script to execute")] string command,
        IProgress<ProgressNotificationValue> progress,
        [Description("Run as a background job; returns a job id immediately instead of waiting")] bool background = false,
        CancellationToken ct = default)
    {
        if (background)
            return JsonSerializer.Serialize(await _jobs.StartAsync(command, ct));

        var runTask = _ps.RunAsync(command, ct);
        var sw = Stopwatch.StartNew();
        // Heartbeat so spec-compliant clients reset their request timeout on progress. The SDK
        // binds `progress` to a real forwarder only when the client sent a progressToken; it is
        // a no-op sink otherwise, so reporting is always safe. Deliberately NO ct on Task.Delay:
        // on cancel the delay branch would win instantly and the loop would spin/report after
        // cancellation — RunAsync observes ct and completes promptly, exiting the loop.
        while (await Task.WhenAny(runTask, Task.Delay(_heartbeatInterval)) != runTask)
        {
            progress.Report(new()
            {
                Progress = (float)sw.Elapsed.TotalSeconds,
                Message = $"powershell running ({(int)sw.Elapsed.TotalSeconds}s)",
            });
        }
        return JsonSerializer.Serialize(await runTask);
    }
}

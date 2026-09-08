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

    /// <summary>C-6: the longest per-call timeout, equal to the service's execution backstop.</summary>
    internal const int MaxTimeoutSeconds = 900;

    public ShellTools(IPowerShellService ps, IJobService jobs)
        : this(ps, jobs, TimeSpan.FromSeconds(10)) { }

    // Test ctor: a short heartbeat interval keeps the heartbeat test from sleeping 10s.
    internal ShellTools(IPowerShellService ps, IJobService jobs, TimeSpan heartbeatInterval)
    {
        _ps = ps;
        _jobs = jobs;
        _heartbeatInterval = heartbeatInterval;
    }

    [McpServerTool(Title = "Run PowerShell", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description(
        "Execute a PowerShell command and return the result including stdout, stderr, and exit " +
        "code. Progress output is suppressed (there is no console to draw a progress bar on) and " +
        "the warning/verbose/debug streams arrive as prefixed text ('WARNING: careful'), not the " +
        "CLIXML the host emits when stderr is redirected. Long-running foreground calls emit MCP " +
        "progress heartbeats every 10s so " +
        "spec-compliant clients reset their request timeout (foreground execution backstop: 15 " +
        "min). For commands longer than that — installers, DISM, bulk hashes — pass " +
        "background:true to run it as a job instead: returns {Id, Pid, State} immediately, then " +
        "poll with the 'job' tool (status|output|cancel|list). To bound one call instead, pass " +
        "timeout_seconds (1-900): on expiry the child tree is killed and the result comes back " +
        "with TimedOut:true, ExitCode:-1 and whatever stdout the script wrote before it hung, " +
        "rather than an error. timeout_seconds cannot be combined with background:true.")]
    public async Task<string> Powershell(
        [Description("PowerShell command or script to execute")] string command,
        IProgress<ProgressNotificationValue> progress,
        [Description("Run as a background job; returns a job id immediately instead of waiting")] bool background = false,
        [Description("Bound this call to 1-900 seconds; 0 (default) means the 15-minute execution backstop only. Not valid with background:true")] int timeout_seconds = 0,
        CancellationToken ct = default)
    {
        // C-6 (R9): the range and the combination are refused before a job is started or a
        // process spawned. 0 is "the execution backstop only", the pre-C-6 behaviour.
        if (timeout_seconds < 0 || timeout_seconds > MaxTimeoutSeconds)
            throw new ArgumentException(
                $"timeout_seconds must be 0 (the 15-minute execution backstop only) or 1-{MaxTimeoutSeconds}, got {timeout_seconds}.",
                nameof(timeout_seconds));
        if (background && timeout_seconds != 0)
            throw new ArgumentException(
                "timeout_seconds cannot be combined with background:true: a job runs until it finishes " +
                "or job(cancel) stops it, and has its own 60-minute backstop. Drop one of the two.",
                nameof(timeout_seconds));

        if (background)
            return JsonSerializer.Serialize(await _jobs.StartAsync(command, ct));

        TimeSpan? timeout = timeout_seconds == 0 ? null : TimeSpan.FromSeconds(timeout_seconds);
        var runTask = _ps.RunAsync(command, timeout, ct);
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

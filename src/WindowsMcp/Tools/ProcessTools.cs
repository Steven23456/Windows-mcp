using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class ProcessTools
{
    private readonly IProcessService _process;
    private readonly IServiceControlService _service;
    private readonly ITaskSchedulerService _scheduler;
    private readonly IEventLogService _eventLog;

    public ProcessTools(
        IProcessService process,
        IServiceControlService service,
        ITaskSchedulerService scheduler,
        IEventLogService eventLog)
    {
        _process = process;
        _service = service;
        _scheduler = scheduler;
        _eventLog = eventLog;
    }

    [McpServerTool, Description("List or kill processes. action: list|kill. 'kill' requires confirm:true and either name or pid.")]
    public async Task<string> Process(
        [Description("Action: list or kill")] string action,
        [Description("Process name to kill (kills all matching)")] string? name = null,
        [Description("Process ID to kill")] int? pid = null,
        [Description("Must be true to confirm destructive kill action")] bool confirm = false)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                var procs = await _process.ListAsync();
                return JsonSerializer.Serialize(procs);

            case "kill":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for kill");
                if (pid.HasValue)
                {
                    await _process.KillAsync(pid.Value);
                    return $"killed pid {pid.Value}";
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var all = await _process.ListAsync();
                    var targets = all.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                    foreach (var t in targets)
                        await _process.KillAsync(t.Pid);
                    return $"killed {targets.Length} process(es) named '{name}'";
                }
                throw new ArgumentException("'kill' requires either name or pid");

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|kill");
        }
    }

    [McpServerTool, Description("Start a process detached from the MCP server. Returns the PID.")]
    public async Task<string> StartProcess(
        [Description("Command line to execute (exe + args)")] string command)
    {
        int pid = await _process.StartDetachedAsync(command);
        return $"started (pid={pid})";
    }

    [McpServerTool, Description("Manage Windows services. action: list|status|start|stop|restart. stop and restart require confirm:true.")]
    public async Task<string> Service(
        [Description("Action: list, status, start, stop, restart")] string action,
        [Description("Service name (required for status/start/stop/restart)")] string? name = null,
        [Description("Must be true to confirm stop or restart")] bool confirm = false)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                var services = await _service.ListAsync();
                return JsonSerializer.Serialize(services);

            case "status":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'status' requires name");
                var status = await _service.GetStatusAsync(name);
                return JsonSerializer.Serialize(status);

            case "start":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'start' requires name");
                await _service.StartAsync(name);
                return $"started '{name}'";

            case "stop":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for stop/restart actions");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'stop' requires name");
                await _service.StopAsync(name);
                return $"stopped '{name}'";

            case "restart":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for stop/restart actions");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'restart' requires name");
                await _service.RestartAsync(name);
                return $"restarted '{name}'";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|status|start|stop|restart");
        }
    }

    [McpServerTool, Description("Manage Windows scheduled tasks. action: list|get|run|create|delete. delete requires confirm:true.")]
    public async Task<string> ScheduledTask(
        [Description("Action: list, get, run, create, delete")] string action,
        [Description("Task name (required for get/run/create/delete)")] string? name = null,
        [Description("Command for create action")] string? command = null,
        [Description("Trigger for create action (e.g. 'daily', 'onlogon')")] string? trigger = null,
        [Description("Must be true to confirm destructive delete action")] bool confirm = false)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                var tasks = await _scheduler.ListAsync();
                return JsonSerializer.Serialize(tasks);

            case "get":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'get' requires name");
                var task = await _scheduler.GetAsync(name);
                return JsonSerializer.Serialize(task);

            case "run":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'run' requires name");
                await _scheduler.RunAsync(name);
                return $"ran task '{name}'";

            case "create":
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(trigger))
                    throw new ArgumentException("'create' requires name, command, and trigger");
                await _scheduler.CreateAsync(name, command, trigger);
                return $"created task '{name}'";

            case "delete":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for delete");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'delete' requires name");
                await _scheduler.DeleteAsync(name);
                return $"deleted task '{name}'";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|get|run|create|delete");
        }
    }

    [McpServerTool, Description("Query Windows Event Log. log: Application|System|Security. level: error|warning|information. since: ISO 8601 datetime.")]
    public async Task<string> EventLog(
        [Description("Event log name: Application, System, Security, etc.")] string log,
        [Description("Filter by level: error, warning, information")] string? level = null,
        [Description("Filter by source name")] string? source = null,
        [Description("Filter events since this datetime (ISO 8601 format)")] string? since = null,
        [Description("Maximum number of entries to return")] int max = 100)
    {
        DateTime? sinceDate = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                throw new ArgumentException($"'since' must be a valid ISO 8601 datetime, got: '{since}'");
            sinceDate = parsed;
        }

        var entries = await _eventLog.QueryAsync(log, level, source, sinceDate, max);
        return JsonSerializer.Serialize(entries);
    }
}

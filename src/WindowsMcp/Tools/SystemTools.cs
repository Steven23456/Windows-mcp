using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class SystemTools
{
    private readonly IWmiService _wmi;
    private readonly IEnvService _env;
    private readonly IPowerService _power;
    private readonly INotificationService _notification;
    private readonly IAudioService _audio;
    private readonly IPowerShellService _ps;

    public SystemTools(
        IWmiService wmi,
        IEnvService env,
        IPowerService power,
        INotificationService notification,
        IAudioService audio,
        IPowerShellService ps)
    {
        _wmi = wmi;
        _env = env;
        _power = power;
        _notification = notification;
        _audio = audio;
        _ps = ps;
    }

    private static readonly Dictionary<string, string> WmiClassMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["os"]      = "Win32_OperatingSystem",
        ["memory"]  = "Win32_PhysicalMemory",
        ["disk"]    = "Win32_LogicalDisk",
        ["gpu"]     = "Win32_VideoController",
        ["battery"] = "Win32_Battery",
    };

    [McpServerTool, Description("Query system information via WMI. category: os|memory|disk|gpu|battery.")]
    public async Task<string> SystemInfo(
        [Description("Category of system info: os, memory, disk, gpu, battery")] string category,
        CancellationToken ct = default)
    {
        if (!WmiClassMap.TryGetValue(category, out var className))
            throw new ArgumentException($"Unknown category '{category}'; expected os|memory|disk|gpu|battery");

        var rows = await _wmi.QueryAsync(className, ct: ct);
        return JsonSerializer.Serialize(rows);
    }

    [McpServerTool, Description("Control audio. action: get|set|mute|unmute. 'set' requires level (0-100).")]
    public async Task<string> Audio(
        [Description("Action: get, set, mute, unmute")] string action,
        [Description("Volume level 0-100 (required for set)")] int? level = null,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "get":
                var state = await _audio.GetAsync(ct);
                return JsonSerializer.Serialize(state);

            case "set":
                if (!level.HasValue)
                    throw new ArgumentException("'set' requires level");
                await _audio.SetVolumeAsync(level.Value, ct);
                return $"volume set to {level.Value}";

            case "mute":
                await _audio.SetMutedAsync(true, ct);
                return "muted";

            case "unmute":
                await _audio.SetMutedAsync(false, ct);
                return "unmuted";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected get|set|mute|unmute");
        }
    }

    [McpServerTool, Description("Show a Windows toast notification.")]
    public async Task<string> Notification(
        [Description("Notification title")] string title,
        [Description("Notification message body")] string message,
        CancellationToken ct = default)
    {
        await _notification.ShowAsync(title, message, ct);
        return "notification shown";
    }

    [McpServerTool, Description("Run a Windows security audit and return firewall, Defender, UAC, and BitLocker status.")]
    public async Task<string> SecurityAudit(CancellationToken ct = default)
    {
        var script = @"
[PSCustomObject]@{
  firewall_enabled  = (Get-NetFirewallProfile | Where-Object Enabled).Count -gt 0
  defender_running  = (Get-Service WinDefend -ErrorAction SilentlyContinue).Status -eq 'Running'
  uac_level         = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).ConsentPromptBehaviorAdmin
  bitlocker_status  = (Get-BitLockerVolume -MountPoint C: -ErrorAction SilentlyContinue).ProtectionStatus
} | ConvertTo-Json
";
        var result = await _ps.RunAsync(script, ct);
        return result.Stdout.Trim();
    }

    [McpServerTool, Description("Run a raw WMI query. class_name: WMI class, e.g. Win32_OperatingSystem.")]
    public async Task<string> WmiQuery(
        [Description("WMI class name, e.g. Win32_Process")] string class_name,
        [Description("WMI namespace (default: root\\cimv2)")] string? @namespace = null,
        [Description("WHERE clause, e.g. ProcessId=1234")] string? where = null,
        CancellationToken ct = default)
    {
        var rows = await _wmi.QueryAsync(class_name, @namespace, where, ct);
        return JsonSerializer.Serialize(rows);
    }

    [McpServerTool, Description("Get, set, or list environment variables. action: get|set|list. scope: Process|User|Machine. 'set' requires confirm:true.")]
    public async Task<string> Env(
        [Description("Action: get, set, list")] string action,
        [Description("Variable name (required for get/set)")] string? name = null,
        [Description("Variable value (for set; null to delete)")] string? value = null,
        [Description("Scope: Process, User, Machine")] string scope = "Process",
        [Description("Must be true to confirm set/delete")] bool confirm = false,
        CancellationToken ct = default)
    {
        var target = scope.ToLowerInvariant() switch
        {
            "process" => EnvironmentVariableTarget.Process,
            "user"    => EnvironmentVariableTarget.User,
            "machine" => EnvironmentVariableTarget.Machine,
            _         => throw new ArgumentException($"Unknown scope '{scope}'; expected Process|User|Machine")
        };

        switch (action.ToLowerInvariant())
        {
            case "get":
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'get' requires name");
                var val = await _env.GetAsync(name, target, ct);
                return JsonSerializer.Serialize(val);

            case "set":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for set");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'set' requires name");
                await _env.SetAsync(name, value, target, ct);
                return value is null ? $"deleted '{name}' from {scope}" : $"set '{name}' in {scope}";

            case "list":
                var vars = await _env.ListAsync(target, ct);
                return JsonSerializer.Serialize(vars);

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected get|set|list");
        }
    }

    [McpServerTool, Description("Execute a system power action. action: shutdown|reboot|logoff|lock|sleep|hibernate. Requires confirm:true.")]
    public async Task<string> PowerAction(
        [Description("Power action: shutdown, reboot, logoff, lock, sleep, hibernate")] string action,
        [Description("Must be true to confirm the power action")] bool confirm = false,
        CancellationToken ct = default)
    {
        if (!confirm)
            throw new ArgumentException("'confirm: true' is required for power actions");

        await _power.ExecuteAsync(action, ct);
        return $"power action '{action}' executed";
    }
}

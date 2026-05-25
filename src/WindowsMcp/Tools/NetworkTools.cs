using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class NetworkTools
{
    private readonly INetworkService _network;
    private readonly IPowerShellService _ps;

    public NetworkTools(INetworkService network, IPowerShellService ps)
    {
        _network = network;
        _ps = ps;
    }

    [McpServerTool, Description("Query network info. action: adapters (list NICs), ports (active TCP listeners/connections), ping (ICMP ping), dns (DNS lookup), wifi (WiFi status).")]
    public async Task<string> Network(
        [Description("Action: adapters, ports, ping, dns, wifi")] string action,
        [Description("Hostname or IP (required for ping and dns)")] string? host = null,
        [Description("Port number (reserved for future use)")] int? port = null)
    {
        switch (action.ToLowerInvariant())
        {
            case "adapters":
                return JsonSerializer.Serialize(await _network.ListAdaptersAsync());

            case "ports":
                return JsonSerializer.Serialize(await _network.ListPortsAsync());

            case "ping":
                if (string.IsNullOrWhiteSpace(host))
                    throw new ArgumentException("'host' is required for ping action");
                return JsonSerializer.Serialize(await _network.PingAsync(host));

            case "dns":
                if (string.IsNullOrWhiteSpace(host))
                    throw new ArgumentException("'host' is required for dns action");
                return JsonSerializer.Serialize(await _network.DnsLookupAsync(host));

            case "wifi":
                return JsonSerializer.Serialize(await _network.GetWifiAsync());

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected adapters|ports|ping|dns|wifi");
        }
    }

    [McpServerTool, Description("Manage Windows Firewall rules. action: list (read rules), add (create rule, requires confirm: true), remove (delete rule, requires confirm: true).")]
    public async Task<string> Firewall(
        [Description("Action: list, add, remove")] string action,
        [Description("Rule display name (required for add and remove)")] string? name = null,
        [Description("Traffic direction: Inbound or Outbound (required for add)")] string? direction = null,
        [Description("Firewall action: Allow or Block (required for add)")] string? action_type = null,
        [Description("Local port number (required for add)")] int? port = null,
        [Description("Must be true to confirm add or remove operations")] bool confirm = false)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
            {
                var script = "Get-NetFirewallRule | Select-Object Name,DisplayName,Enabled,Direction,Action | ConvertTo-Json -Depth 2";
                var result = await _ps.RunAsync(script);
                return result.Stdout;
            }

            case "add":
            {
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for firewall add");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'name' is required for firewall add");
                if (string.IsNullOrWhiteSpace(direction))
                    throw new ArgumentException("'direction' is required for firewall add");
                if (string.IsNullOrWhiteSpace(action_type))
                    throw new ArgumentException("'action_type' is required for firewall add");
                if (port is null)
                    throw new ArgumentException("'port' is required for firewall add");

                // Use single-quoted PowerShell strings and escape any single-quotes in name
                var safeName = name.Replace("'", "''");
                var safeDirection = direction.Replace("'", "''");
                var safeActionType = action_type.Replace("'", "''");
                var script = $"New-NetFirewallRule -DisplayName '{safeName}' -Direction '{safeDirection}' -Action '{safeActionType}' -LocalPort {port} -Protocol TCP";
                var result = await _ps.RunAsync(script);
                if (!result.Success)
                    throw new InvalidOperationException($"Firewall add failed: {result.Stderr}");
                return $"Added firewall rule '{name}'";
            }

            case "remove":
            {
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for firewall remove");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'name' is required for firewall remove");

                var safeName = name.Replace("'", "''");
                var script = $"Remove-NetFirewallRule -DisplayName '{safeName}'";
                var result = await _ps.RunAsync(script);
                if (!result.Success)
                    throw new InvalidOperationException($"Firewall remove failed: {result.Stderr}");
                return $"Removed firewall rule '{name}'";
            }

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|add|remove");
        }
    }
}

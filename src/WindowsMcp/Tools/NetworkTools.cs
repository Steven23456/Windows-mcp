using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class NetworkTools
{
    private readonly INetworkService _network;
    private readonly IFirewallService _firewall;

    public NetworkTools(INetworkService network, IFirewallService firewall)
    {
        _network = network;
        _firewall = firewall;
    }

    [McpServerTool, Description("Query network info. action: adapters (list NICs), ports (active TCP listeners/connections), ping (ICMP ping), dns (DNS lookup), wifi (WiFi status).")]
    public async Task<string> Network(
        [Description("Action: adapters, ports, ping, dns, wifi")] string action,
        [Description("Hostname or IP (required for ping and dns)")] string? host = null,
        [Description("Port number (reserved for future use)")] int? port = null,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "adapters":
                return JsonSerializer.Serialize(await _network.ListAdaptersAsync(ct));

            case "ports":
                return JsonSerializer.Serialize(await _network.ListPortsAsync(ct));

            case "ping":
                if (string.IsNullOrWhiteSpace(host))
                    throw new ArgumentException("'host' is required for ping action");
                return JsonSerializer.Serialize(await _network.PingAsync(host, ct));

            case "dns":
                if (string.IsNullOrWhiteSpace(host))
                    throw new ArgumentException("'host' is required for dns action");
                return JsonSerializer.Serialize(await _network.DnsLookupAsync(host, ct));

            case "wifi":
                return JsonSerializer.Serialize(await _network.GetWifiAsync(ct));

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected adapters|ports|ping|dns|wifi");
        }
    }

    [McpServerTool, Description("Manage Windows Firewall rules. action: list (read enabled rules; full list is multi-MB so pass name_like to filter), add (create rule, requires confirm: true), remove (delete rule, requires confirm: true).")]
    public async Task<string> Firewall(
        [Description("Action: list, add, remove")] string action,
        [Description("Rule display name (required for add and remove)")] string? name = null,
        [Description("Traffic direction: Inbound or Outbound (required for add)")] string? direction = null,
        [Description("Firewall action: Allow or Block (required for add)")] string? action_type = null,
        [Description("Local port number (required for add)")] int? port = null,
        [Description("Must be true to confirm add or remove operations")] bool confirm = false,
        [Description("For list: substring filter on DisplayName (case-insensitive). Default null returns enabled rules only.")] string? name_like = null,
        [Description("For list: maximum rules returned. Default 100; the full system list can be thousands.")] int max = 100,
        CancellationToken ct = default)
    {
        switch (action.ToLowerInvariant())
        {
            case "list":
                return JsonSerializer.Serialize(await _firewall.ListAsync(name_like, max, ct));

            case "add":
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
                await _firewall.AddAsync(name, direction, action_type, port.Value, ct);
                return $"Added firewall rule '{name}'";

            case "remove":
                if (!confirm)
                    throw new ArgumentException("'confirm: true' is required for firewall remove");
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("'name' is required for firewall remove");
                await _firewall.RemoveAsync(name, ct);
                return $"Removed firewall rule '{name}'";

            default:
                throw new ArgumentException($"Unknown action '{action}'; expected list|add|remove");
        }
    }
}

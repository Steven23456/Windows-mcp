using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class NetworkService : INetworkService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly ILogger _log;
    private readonly IPowerShellService _ps;

    public NetworkService(ILogger<NetworkService> log, IPowerShellService ps)
    {
        _log = log;
        _ps = ps;
    }

    public Task<NetworkAdapterDto[]> ListAdaptersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Select(ni =>
            {
                var ips = ni.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address.ToString())
                    .ToArray();
                return new NetworkAdapterDto(
                    Name: ni.Name,
                    Description: ni.Description,
                    Status: ni.OperationalStatus.ToString(),
                    IpAddresses: ips);
            })
            .ToArray();
        return Task.FromResult(adapters);
    }

    public async Task<PortInfoDto[]> ListPortsAsync(CancellationToken ct = default)
    {
        // The managed IPGlobalProperties API gives endpoints but NOT the owning PID — the single
        // most useful field for "what is this machine talking to and who owns it". Get-NetTCPConnection
        // provides it (plus state) uniformly for IPv4 and IPv6; we join PID -> process name in-shell.
        var result = await _ps.RunAsync(PortsScript, ct);
        return ParsePorts(result.Stdout);
    }

    internal static PortInfoDto[] ParsePorts(string? stdout)
    {
        var json = stdout?.Trim();
        if (string.IsNullOrEmpty(json)) return Array.Empty<PortInfoDto>();
        if (json[0] == '[')
            return JsonSerializer.Deserialize<PortInfoDto[]>(json, JsonOpts) ?? Array.Empty<PortInfoDto>();
        var single = JsonSerializer.Deserialize<PortInfoDto>(json, JsonOpts);
        return single is null ? Array.Empty<PortInfoDto>() : new[] { single };
    }

    // Must be a SINGLE pipeline statement: a multi-statement script (e.g. a leading
    // `$map = @{}` prelude) silently returns empty over `powershell -Command -` stdin — the same
    // failure mode storage_health hit. ProcessName is resolved per-row via Get-Process.
    private const string PortsScript =
        "Get-NetTCPConnection | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort," +
        "@{n='State';e={$_.State.ToString()}}," +
        "@{n='OwningPid';e={[int]$_.OwningProcess}}," +
        "@{n='ProcessName';e={(Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName}} " +
        "| ConvertTo-Json -Depth 2";

    public async Task<PingResult> PingAsync(string host, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 3000);
            if (reply.Status == IPStatus.Success)
                return new PingResult(host, true, reply.RoundtripTime);
            return new PingResult(host, false, null);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ping to {Host} failed", host);
            return new PingResult(host, false, null);
        }
    }

    public async Task<string[]> DnsLookupAsync(string host, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        return addresses.Select(a => a.ToString()).ToArray();
    }

    public Task<WifiInfoDto> GetWifiAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // v0.2.0 placeholder: real WiFi info requires either:
        //   (a) Windows.Networking.Connectivity WinRT APIs (needs UWP package),
        //   (b) shelling out to `netsh wlan show interfaces` and parsing output.
        // Tracked for v0.3.0: implement via netsh shell-out to avoid WinRT
        // packaging requirements.
        return Task.FromResult(new WifiInfoDto("Unknown", 0, "ManagedAPIRequired"));
    }
}

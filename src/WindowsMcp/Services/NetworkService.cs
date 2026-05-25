using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class NetworkService : INetworkService
{
    private readonly ILogger _log;

    public NetworkService(ILogger<NetworkService> log)
    {
        _log = log;
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

    public Task<PortInfoDto[]> ListPortsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var props = IPGlobalProperties.GetIPGlobalProperties();

        var listeners = props.GetActiveTcpListeners()
            .Select(ep => new PortInfoDto(
                LocalAddress: ep.Address.ToString(),
                LocalPort: ep.Port,
                RemoteAddress: null,
                RemotePort: null,
                State: "Listen"))
            .ToArray();

        var connections = props.GetActiveTcpConnections()
            .Select(conn => new PortInfoDto(
                LocalAddress: conn.LocalEndPoint.Address.ToString(),
                LocalPort: conn.LocalEndPoint.Port,
                RemoteAddress: conn.RemoteEndPoint.Address.ToString(),
                RemotePort: conn.RemoteEndPoint.Port,
                State: conn.State.ToString()))
            .ToArray();

        var combined = listeners.Concat(connections).ToArray();
        return Task.FromResult(combined);
    }

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

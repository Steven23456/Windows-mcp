using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface INetworkService
{
    Task<NetworkAdapterDto[]> ListAdaptersAsync(CancellationToken ct = default);
    Task<PortInfoDto[]> ListPortsAsync(CancellationToken ct = default);
    Task<PingResult> PingAsync(string host, CancellationToken ct = default);
    Task<string[]> DnsLookupAsync(string host, CancellationToken ct = default);
    Task<WifiInfoDto> GetWifiAsync(CancellationToken ct = default);
}

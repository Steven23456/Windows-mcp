using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IServiceControlService
{
    Task<ServiceDto[]> ListAsync(CancellationToken ct = default);
    Task<ServiceDto> GetStatusAsync(string name, CancellationToken ct = default);
    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task RestartAsync(string name, CancellationToken ct = default);
}

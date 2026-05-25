using System.ServiceProcess;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ServiceControlService : IServiceControlService
{
    public Task<ServiceDto[]> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ServiceController.GetServices()
            .Select(s => new ServiceDto(s.ServiceName, s.DisplayName, s.Status.ToString(), s.StartType.ToString()))
            .ToArray());
    }

    public Task<ServiceDto> GetStatusAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var sc = new ServiceController(name);
        return Task.FromResult(new ServiceDto(
            sc.ServiceName,
            sc.DisplayName,
            sc.Status.ToString(),
            sc.StartType.ToString()));
    }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var sc = new ServiceController(name);
        sc.Start();
        // WaitForStatus is sync-blocking; wrap in Task.Run to avoid starving the thread pool.
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15)), ct);
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var sc = new ServiceController(name);
        sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)), ct);
    }

    public async Task RestartAsync(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await StopAsync(name, ct);
        await StartAsync(name, ct);
    }
}

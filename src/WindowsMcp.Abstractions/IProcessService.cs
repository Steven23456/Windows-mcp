using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IProcessService
{
    Task<ProcessDto[]> ListAsync(CancellationToken ct = default);
    Task KillAsync(int pid, CancellationToken ct = default);
    Task<int> StartDetachedAsync(string command, CancellationToken ct = default);
}

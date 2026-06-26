using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IProcessService
{
    Task<ProcessDto[]> ListAsync(CancellationToken ct = default);
    Task KillAsync(int pid, CancellationToken ct = default);
    Task<int> StartDetachedAsync(string command, CancellationToken ct = default);
    /// <summary>Deep detail for one process: parent PID, command line, start time, loaded modules.</summary>
    Task<ProcessDetailDto> InspectAsync(int pid, CancellationToken ct = default);
}

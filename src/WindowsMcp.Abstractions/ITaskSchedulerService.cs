using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface ITaskSchedulerService
{
    Task<ScheduledTaskDto[]> ListAsync(CancellationToken ct = default);
    Task<ScheduledTaskDto> GetAsync(string name, CancellationToken ct = default);
    Task RunAsync(string name, CancellationToken ct = default);
    Task CreateAsync(string name, string command, string trigger, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}

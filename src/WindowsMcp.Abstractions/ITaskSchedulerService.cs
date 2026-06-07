using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface ITaskSchedulerService
{
    Task<ScheduledTaskDto[]> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// List all tasks across every folder with action + trigger detail, for persistence
    /// reporting. Tasks whose definition cannot be read (access denied / corrupt) still
    /// appear with name/path/state and null action / empty triggers.
    /// </summary>
    Task<ScheduledTaskDetailDto[]> ListDetailedAsync(CancellationToken ct = default);

    Task<ScheduledTaskDto> GetAsync(string name, CancellationToken ct = default);
    Task RunAsync(string name, CancellationToken ct = default);
    Task CreateAsync(string name, string command, string trigger, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}

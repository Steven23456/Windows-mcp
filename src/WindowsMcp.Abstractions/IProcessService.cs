using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IProcessService
{
    Task<ProcessDto[]> ListAsync(CancellationToken ct = default);
    Task KillAsync(int pid, CancellationToken ct = default);
    Task<int> StartDetachedAsync(string command, CancellationToken ct = default);
    /// <summary>Deep detail for one process: parent PID, command line, start time, loaded modules.</summary>
    Task<ProcessDetailDto> InspectAsync(int pid, CancellationToken ct = default);
    /// <summary>All processes with recycle-aware lineage + signals; optionally only orphans,
    /// optionally filtered (substring on name OR command line). Filter is applied after
    /// classification so RootPid still resolves to a filtered-out root.</summary>
    Task<ProcessLineageDto[]> ListLineageAsync(bool orphansOnly, string? nameFilter, CancellationToken ct = default);
    /// <summary>Processes collapsed under their nearest-live root ancestor.</summary>
    Task<ProcessGroupDto[]> GroupByRootAsync(CancellationToken ct = default);
    /// <summary>Kill a single PID only if its live start time matches expectedStartUtc (guards PID reuse).</summary>
    Task KillGuardedAsync(int pid, DateTime expectedStartUtc, CancellationToken ct = default);
    /// <summary>Kill a PID and its recycle-validated descendants, leaves-first; returns count killed.</summary>
    Task<int> KillTreeAsync(int pid, DateTime? expectedStartUtc, CancellationToken ct = default);
}

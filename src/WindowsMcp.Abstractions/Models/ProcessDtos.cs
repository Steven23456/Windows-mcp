namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// One process as <c>process(list)</c> reports it. <see cref="CpuPercent"/> (C-3 R4) is the
/// two-sample measurement normalised across ALL cores, so a process saturating one of eight cores
/// reads 12.5 - what Task Manager shows. The lineage and group rows do not carry it.
/// </summary>
public record ProcessDto(int Pid, string Name, string? Path, long MemoryMb, double CpuPercent = 0);

/// <summary>C-3 R4: the order <c>process(list, sort_by)</c> asks for.</summary>
public enum ProcessSort { Memory, Cpu, Name, Pid }

/// <summary>
/// C-3 R4: what a plain process list asks for. <see cref="NameFilter"/> is a case-insensitive
/// SUBSTRING of the name (unchanged); <see cref="Limit"/> 0 means all, applied after the filter
/// and the sort.
/// </summary>
public record ProcessListOptions(string? NameFilter = null, ProcessSort SortBy = ProcessSort.Memory, int Limit = 0);

/// <summary>
/// C-3 R5: how to kill. <see cref="Graceful"/> asks the process to close its own windows first and
/// waits <see cref="GraceMs"/> before forcing; <see cref="ExpectedStartUtc"/> is the existing
/// PID-reuse guard - a mismatch aborts and kills nothing.
/// </summary>
public record KillOptions(bool Graceful = false, int GraceMs = 3000, DateTime? ExpectedStartUtc = null);

/// <summary>
/// C-3 R5: what a kill did. <see cref="ExitedGracefully"/> is true only when the process left on
/// its own inside the grace window; <see cref="Forced"/> says TerminateProcess was used in the end.
/// A console process with no window reports <c>ExitedGracefully:false, Forced:true, WaitedMs:0</c>.
/// </summary>
public record KillResult(int Pid, string? Name, bool Graceful, bool ExitedGracefully, bool Forced, int WaitedMs);

/// <summary>
/// B-11: what to start. <paramref name="Command"/> is the executable — the whole command line
/// (split on the first space, quoted-exe aware) when <paramref name="Args"/> is null, the
/// executable path and nothing else when it is not. <paramref name="Args"/> becomes
/// <c>ProcessStartInfo.ArgumentList</c> verbatim: no quoting, no splitting, no escaping.
/// <paramref name="Cwd"/> must exist when given.
/// </summary>
public record ProcessStart(string Command, string[]? Args, string? Cwd, bool UseShellExecute);

/// <summary>A DLL/module loaded into a process.</summary>
public record ModuleInfo(string Name, string? Path);

/// <summary>
/// Deep per-process detail. <see cref="Modules"/> is the loaded-DLL inventory (the core
/// injection/sideloading signal); <see cref="ModulesError"/> is set instead when the module list
/// can't be read (e.g. a protected or higher-integrity process denies access).
/// </summary>
public record ProcessDetailDto(
    int Pid,
    string? Name,
    int? ParentPid,
    string? CommandLine,
    DateTime? StartTimeUtc,
    string? ModulesError,
    ModuleInfo[] Modules);

/// <summary>One process with parent lineage, orphan status, and descriptive signals.</summary>
public record ProcessLineageDto(
    int Pid, string Name, int? ParentPid, string? ParentName, string? CommandLine,
    DateTime? StartTimeUtc, int? AgeMinutes, bool Orphaned, string RuntimeKind,
    bool IsSystemAdjacent, int RootPid, long MemoryMb);

/// <summary>
/// Processes collapsed under their nearest-live root ancestor. <see cref="DescendantCount"/> and
/// <see cref="ChildPids"/> are inclusive of the root itself (the full group membership) — e.g. a
/// root with two children reports count 3 and pids {root, c1, c2}.
/// </summary>
public record ProcessGroupDto(
    int RootPid, string RootName, DateTime? RootStartTimeUtc, int DescendantCount, int[] ChildPids);

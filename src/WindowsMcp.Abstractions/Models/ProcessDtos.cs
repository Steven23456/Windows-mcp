namespace WindowsMcp.Abstractions.Models;

public record ProcessDto(int Pid, string Name, string? Path, long MemoryMb);

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

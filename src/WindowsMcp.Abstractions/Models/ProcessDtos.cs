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

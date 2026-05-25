namespace WindowsMcp.Abstractions.Models;

public record ProcessDto(int Pid, string Name, string? Path, long MemoryMb);

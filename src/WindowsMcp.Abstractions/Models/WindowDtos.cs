namespace WindowsMcp.Abstractions.Models;

public record WindowAction(string Action, string? Title, bool Success);
public record MonitorInfo(int Index, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary);

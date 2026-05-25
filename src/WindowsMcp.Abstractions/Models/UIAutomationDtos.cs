namespace WindowsMcp.Abstractions.Models;

public record ElementInfo(
    string ElementId,
    string Name,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    Bounds? Bounds,
    string? Value,
    bool? IsChecked,
    bool? IsSelected);

public record Bounds(int X, int Y, int Width, int Height);

public record ElementTree(ElementInfo Root, ElementTree[] Children);

public record FindElementResult(ElementInfo[] Matches);

public enum FindKind { Interactive, Text, Scrollable, Any }

public record TableData(string[] Headers, string[][] Rows);

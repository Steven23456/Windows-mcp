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

/// <summary>
/// What <c>interact_element</c> actually did. <paramref name="Method"/> is the UIA pattern that
/// fired (InvokePattern, SelectionItemPattern, TogglePattern, ValuePattern, Focus) or the fallback
/// (PhysicalClick, Keyboard); <paramref name="Detail"/> adds the click point, toggle state, or item name.
/// </summary>
public record InteractResult(string ElementId, string Action, string Method, string? Detail);

/// <summary>
/// Outcome of <c>assert_element</c>. <paramref name="Observed"/> is what the element actually
/// showed — on FAIL the reason (<c>disabled</c>, <c>toggle state Off</c>, <c>focus is on Button
/// 'Save'</c>, <c>value is 'x' (from ValuePattern)</c>, <c>element no longer available</c>), on
/// PASS the matching observation.
/// </summary>
public record AssertResult(string ElementId, string State, bool Pass, string Observed);

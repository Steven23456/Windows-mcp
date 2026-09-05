using System.Text.Json.Serialization;

namespace WindowsMcp.Abstractions.Models;

/// <param name="Scroll">
/// A-3: scroll position and scrollability when the element exposes a ScrollPattern, null when it
/// does not. Additive — every construction that predates A-3 keeps compiling.
/// </param>
public record ElementInfo(
    string ElementId,
    string Name,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    Bounds? Bounds,
    string? Value,
    bool? IsChecked,
    bool? IsSelected,
    ScrollInfo? Scroll = null);

public record Bounds(int X, int Y, int Width, int Height);

/// <summary>
/// A-3: what a ScrollPattern reports. Percentages are 0-100; a non-scrollable axis still reports
/// its percent (UIA gives 0 or -1 there — the traverser normalises), so the flags are what say
/// whether scrolling that axis means anything.
/// </summary>
public record ScrollInfo(
    double VerticalPercent,
    double HorizontalPercent,
    bool VerticallyScrollable,
    bool HorizontallyScrollable);

/// <param name="Truncated">
/// A-4: the walk that produced this tree hit the element budget and stopped early. Set on the
/// ROOT only; children keep the default. Omitted from JSON when false so an untruncated tree
/// serialises exactly as it did before A-4.
/// </param>
/// <param name="ElementLimit">A-4: the budget that was in force. Omitted from JSON when 0.</param>
public record ElementTree(
    ElementInfo Root,
    ElementTree[] Children,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ElementLimit = 0);

public record FindElementResult(ElementInfo[] Matches);

public enum FindKind { Interactive, Text, Scrollable, Any }

/// <summary>
/// Which part of the UI tree <c>find_element</c> / <c>wait_for</c> walk (checklist D-5).
/// <see cref="Foreground"/> is the default: the window the agent is acting on, resolved at call
/// time. <see cref="Window"/> pins the search to one window by title so a multi-step workflow is
/// unaffected by focus moving. <see cref="Desktop"/> walks every top-level window — what the tool
/// did implicitly before D-5, and the reason one stale element could fail the whole call.
/// </summary>
public enum FindScope { Foreground, Window, Desktop }

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

// ---- A-2 snapshot -------------------------------------------------------------------------

/// <summary>How much of the desktop a <c>snapshot</c> walks (A-2).</summary>
public enum SnapshotScope { Desktop, Foreground, Window }

/// <param name="MaxElements">0 = use the server default (<see cref="UiTreeOptions"/>).</param>
public record SnapshotRequest(
    SnapshotScope Scope = SnapshotScope.Desktop,
    string? WindowTitle = null,
    bool IncludeTree = false,
    int MaxElements = 0);

/// <summary>
/// A-4 (roadmap C7): the process-level element budget, set from <c>WINDOWSMCP_MAX_TREE_ELEMENTS</c>
/// by <c>ServerOptions</c> and injected into the service — never read from the environment inside
/// a service. A per-call <c>max_elements</c> overrides it.
/// </summary>
public record UiTreeOptions(int MaxElements)
{
    public static UiTreeOptions Default { get; } = new(500);
}

/// <summary>
/// A-2: one interactive element in a snapshot. <paramref name="CenterX"/>/<paramref name="CenterY"/>
/// are virtual-desktop pixels (roadmap C1) — what <c>click</c>/<c>drag</c>/<c>scroll</c> accept.
/// <paramref name="ElementId"/> is valid until the next snapshot (roadmap C5).
/// </summary>
public record SnapshotElement(
    string ElementId,
    string Window,
    string ControlType,
    string Name,
    int CenterX,
    int CenterY,
    Bounds Bounds,
    string Action,
    bool Focused,
    bool IsPassword,
    string? Value,
    string? Toggle,
    string? Expand,
    string? Shortcut,
    double? RangeValue,
    double? RangeMin,
    double? RangeMax);

/// <summary>A-3: one scrollable region in a snapshot.</summary>
public record SnapshotScrollable(
    string ElementId,
    string Window,
    string ControlType,
    string Name,
    int CenterX,
    int CenterY,
    Bounds Bounds,
    ScrollInfo Scroll);

/// <param name="ElementCount">Every element the walk visited, before the interactive/scrollable split.</param>
public record SnapshotResult(
    WindowInfo[] Windows,
    WindowInfo? ActiveWindow,
    CursorPosition Cursor,
    int CursorMonitorIndex,
    SnapshotElement[] Interactive,
    SnapshotScrollable[] Scrollable,
    ElementTree? Tree,
    bool Truncated,
    int ElementLimit,
    int ElementCount,
    long CaptureMs);

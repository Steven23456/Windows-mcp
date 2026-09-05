using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// Builders for the snapshot DTOs the renderer formats, each a plausible baseline overridable
/// field by field, plus the one place the truncation sentence is written down in the tests -
/// <see cref="ElementBudgetTests"/> and <see cref="SnapshotRendererTests"/> both compare against
/// it, so the budget and the rendered footer cannot drift apart unnoticed.
/// </summary>
internal static class SnapshotFixtures
{
    internal static string TruncationNote(int limit)
        => $"Truncated at {limit} elements. Narrow the view (scope=foreground, or window=<title>) or raise max_elements.";

    /// <summary>Joins with LF, which is the line ending the rendered text must use on Windows too.</summary>
    internal static string Lines(params string[] lines) => string.Join("\n", lines);

    internal static WindowInfo Window(
        string title = "Untitled - Notepad",
        long hwnd = 1,
        int pid = 4242,
        string process = "notepad",
        WindowState state = WindowState.Normal,
        Bounds? bounds = null,
        int zOrder = 0,
        bool isActive = false,
        bool isBrowser = false,
        int monitorIndex = 0)
        => new(title, hwnd, pid, process, state, bounds ?? new Bounds(100, 100, 800, 600),
            zOrder, isActive, isBrowser, monitorIndex);

    internal static SnapshotElement Element(
        string id = "el_12",
        string window = "Untitled - Notepad",
        string controlType = "Button",
        string name = "Save",
        int centerX = 612,
        int centerY = 388,
        Bounds? bounds = null,
        string action = "click",
        bool focused = false,
        bool isPassword = false,
        string? value = null,
        string? toggle = null,
        string? expand = null,
        string? shortcut = null,
        double? rangeValue = null,
        double? rangeMin = null,
        double? rangeMax = null)
        => new(id, window, controlType, name, centerX, centerY, bounds ?? new Bounds(600, 380, 24, 16),
            action, focused, isPassword, value, toggle, expand, shortcut, rangeValue, rangeMin, rangeMax);

    internal static SnapshotScrollable Scrollable(
        string id = "el_20",
        string window = "Untitled - Notepad",
        string controlType = "Document",
        string name = "Text Editor",
        int centerX = 500,
        int centerY = 400,
        Bounds? bounds = null,
        ScrollInfo? scroll = null)
        => new(id, window, controlType, name, centerX, centerY, bounds ?? new Bounds(100, 140, 800, 520),
            scroll ?? new ScrollInfo(37, 0, true, false));

    internal static SnapshotResult Result(
        WindowInfo[]? windows = null,
        WindowInfo? active = null,
        CursorPosition? cursor = null,
        int cursorMonitorIndex = 0,
        SnapshotElement[]? interactive = null,
        SnapshotScrollable[]? scrollable = null,
        ElementTree? tree = null,
        bool truncated = false,
        int elementLimit = 500,
        int elementCount = 57,
        long captureMs = 12)
        => new(windows ?? [], active, cursor ?? new CursorPosition(612, 388), cursorMonitorIndex,
            interactive ?? [], scrollable ?? [], tree, truncated, elementLimit, elementCount, captureMs);
}

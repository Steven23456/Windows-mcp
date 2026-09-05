using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class UIAutomationTools
{
    private readonly IUIAutomationService _uia;

    public UIAutomationTools(IUIAutomationService uia)
    {
        _uia = uia;
    }

    private static FindKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "any" => FindKind.Any,
        "interactive" => FindKind.Interactive,
        "text" => FindKind.Text,
        "scrollable" => FindKind.Scrollable,
        _ => throw new ArgumentException($"Unknown kind '{kind}'; expected any|interactive|text|scrollable")
    };

    private static FindScope ParseScope(string scope) => scope.ToLowerInvariant() switch
    {
        "foreground" => FindScope.Foreground,
        "window" => FindScope.Window,
        "desktop" => FindScope.Desktop,
        _ => throw new ArgumentException($"Unknown scope '{scope}'; expected foreground|window|desktop")
    };

    /// <summary>
    /// A window title is meaningful only with scope=window. Rejecting the mismatch rather than
    /// ignoring the argument follows D-4's `expected` rule — silently ignoring an argument is how
    /// the D-2 `select` bug went unnoticed.
    /// </summary>
    private static (FindScope Scope, string? Window) ParseTarget(string scope, string? window)
    {
        var parsed = ParseScope(scope);
        if (parsed == FindScope.Window && string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("scope=window requires window: the title of the window to search.", nameof(window));
        if (parsed != FindScope.Window && !string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("window is only used with scope=window.", nameof(window));
        return (parsed, string.IsNullOrWhiteSpace(window) ? null : window);
    }

    private static SnapshotScope ParseSnapshotScope(string scope) => scope.ToLowerInvariant() switch
    {
        "desktop" => SnapshotScope.Desktop,
        "foreground" => SnapshotScope.Foreground,
        "window" => SnapshotScope.Window,
        _ => throw new ArgumentException($"Unknown scope '{scope}'; expected desktop|foreground|window")
    };

    [McpServerTool, Description("One call for the whole desktop (parity A-2): every open window, the foreground one, the cursor, and every interactive element with its centre coordinates and an action hint (click/fill/toggle/select/slide/scroll), plus scrollable regions with their scroll percentages. Default format is compact text; format:'json' returns the same as JSON (with the element tree when include_tree is set). Element ids (el_N) are valid until the next snapshot and work with click (use the centre coordinates), interact_element and get_element. scope: desktop (default, every non-minimised window, topmost first) | foreground | window (with 'window' = a title, exact then substring). max_elements caps the walk (0 = the server default, --max-tree-elements, 500); when the cap is hit the result says it was truncated (the text form adds how to narrow the view; json carries Truncated and ElementLimit). use_dom is reserved for browser DOM mode (A-5) and is not implemented yet.")]
    public async Task<string> Snapshot(
        [Description("desktop | foreground | window")] string scope = "desktop",
        [Description("Window title, exact or substring, case-insensitive; only with scope=window")] string? window = null,
        [Description("Also return the element tree (json only)")] bool include_tree = false,
        [Description("Element budget for this call; 0 = the server default (--max-tree-elements)")] int max_elements = 0,
        [Description("text (default, compact) | json")] string format = "text",
        [Description("Reserved for browser DOM mode (A-5); not implemented yet")] bool use_dom = false)
    {
        var parsed = ParseSnapshotScope(scope);
        if (parsed == SnapshotScope.Window && string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("scope=window requires window: the title of the window to snapshot.", nameof(window));
        if (parsed != SnapshotScope.Window && !string.IsNullOrWhiteSpace(window))
            throw new ArgumentException("window is only used with scope=window.", nameof(window));
        if (max_elements < 0)
            throw new ArgumentException($"max_elements must be 0 (the server default) or positive, got {max_elements}");
        bool json = format.ToLowerInvariant() switch
        {
            "text" => false,
            "json" => true,
            _ => throw new ArgumentException($"Unknown format '{format}'; expected text|json"),
        };
        if (use_dom)
            throw new InvalidOperationException("use_dom is reserved for browser DOM mode (parity A-5) and is not implemented yet.");

        var request = new SnapshotRequest(parsed, string.IsNullOrWhiteSpace(window) ? null : window, include_tree, max_elements);
        var result = await _uia.SnapshotAsync(request);
        return json ? JsonSerializer.Serialize(result) : SnapshotRenderer.Render(result);
    }

    [McpServerTool, Description("Return the UI element tree of the foreground application (three levels deep, bounded by --max-tree-elements; the root reports Truncated/ElementLimit when the budget stopped the walk). For the whole desktop with centre coordinates and action hints, use snapshot.")]
    public async Task<string> GetState()
    {
        var tree = await _uia.GetStateAsync();
        return JsonSerializer.Serialize(tree);
    }

    [McpServerTool, Description(
        "Find UI elements whose name contains text (empty text = every element the filters allow). " +
        "Searches the FOREGROUND window by default, resolved at call time — for a multi-step " +
        "workflow pass scope:\"window\" with window:<title> so a notification or another app " +
        "stealing focus cannot change what is searched; scope:\"desktop\" searches every top-level " +
        "window. Returns at most 20 matches, capped AFTER filtering. A UI element that disappears " +
        "mid-search is skipped, never fatal.")]
    public async Task<string> FindElement(
        [Description("Text to search for in element names (substring, case-insensitive); empty matches all")] string text,
        [Description("Element kind: any | interactive (Button, ListItem, MenuItem, Edit, CheckBox, RadioButton, ComboBox, Hyperlink, SplitButton, TabItem, TreeItem, DataItem, HeaderItem, Spinner, Slider, ScrollBar, Document) | text (Text, Edit, Document) | scrollable")] string kind = "any",
        [Description("Search scope: foreground (default) | window (needs 'window') | desktop")] string scope = "foreground",
        [Description("Window title to search, exact or substring, case-insensitive; only with scope=window")] string? window = null,
        [Description("Include off-screen elements (collapsed panes, virtualised rows, minimised windows). Default false; an Edit with real bounds is kept either way")] bool include_offscreen = false)
    {
        var (parsedScope, windowTitle) = ParseTarget(scope, window);
        var result = await _uia.FindElementAsync(text, ParseKind(kind), parsedScope, windowTitle, include_offscreen);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Get detailed information about a UI element by its ID.")]
    public async Task<string> GetElement(
        [Description("Element ID returned by find_element or get_state")] string element_id)
    {
        var info = await _uia.GetElementAsync(element_id);
        return JsonSerializer.Serialize(info);
    }

    [McpServerTool, Description("Extract text content from a UI element (faster than OCR).")]
    public async Task<string> GetText(
        [Description("Element ID to read text from")] string element_id)
    {
        return await _uia.GetTextAsync(element_id);
    }

    [McpServerTool, Description("Assert a UI element state: exists, enabled, checked, visible, focused, or value (needs expected: an exact match against the element's ValuePattern value, else its Name). Returns 'PASS' or 'FAIL: <state> — observed <what was found>', e.g. 'focus is on Button 'Save'', 'value is 'x' (from ValuePattern)', 'toggle state Off', 'element no longer available' (its window closed since the id was issued). An unknown state, 'value' without expected, or expected with another state is an error.")]
    public async Task<string> AssertElement(
        [Description("Element ID to check")] string element_id,
        [Description("State to assert: exists, enabled, checked, value, visible, focused")] string state,
        [Description("Expected value; only with state=value")] string? expected = null)
    {
        var result = await _uia.AssertElementAsync(element_id, state, expected);
        return result.Pass ? "PASS" : $"FAIL: {result.State} — observed {result.Observed}";
    }

    [McpServerTool, Description("Act on a UI element by id. action: click (InvokePattern, else SelectionItem, else Toggle, else a physical click at the element's centre), invoke, toggle, select (no value: select this item; value: pick the child item with that name in a combo/list), focus, type (value = text; a writable ValuePattern replaces the whole value, otherwise it is typed at the caret). Returns {ElementId, Action, Method, Detail} saying which pattern or fallback fired; an unsupported pattern errors naming the control type.")]
    public async Task<string> InteractElement(
        [Description("Element ID from find_element or get_state")] string element_id,
        [Description("click | invoke | toggle | select | focus | type")] string action,
        [Description("Text for 'type'; child item name for 'select' (optional)")] string? value = null)
    {
        var result = await _uia.InteractAsync(element_id, action, value);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Extract tabular data from a UI element via GridPattern.")]
    public async Task<string> GetTable(
        [Description("Element ID of a grid/table control")] string element_id)
    {
        var table = await _uia.GetTableAsync(element_id);
        return JsonSerializer.Serialize(table);
    }

    [McpServerTool, Description(
        "Wait for a UI element whose name contains text to appear, polling find_element. Returns " +
        "element info, or 'null' if it never appeared. Takes the same kind/scope/window/" +
        "include_offscreen filters as find_element — scope:\"window\" is re-resolved on every poll, " +
        "so it also serves as 'wait for that app to open'. A poll that fails is retried; if EVERY " +
        "poll failed the call errors rather than reporting a misleading 'null'.")]
    public async Task<string> WaitFor(
        [Description("Text to wait for in element names (substring, case-insensitive)")] string text,
        [Description("Timeout in milliseconds")] int timeout_ms = 10000,
        [Description("Poll interval in milliseconds")] int interval_ms = 500,
        [Description("Element kind: any | interactive | text | scrollable")] string kind = "any",
        [Description("Search scope: foreground (default) | window (needs 'window') | desktop")] string scope = "foreground",
        [Description("Window title to search; only with scope=window")] string? window = null,
        [Description("Include off-screen elements. Default false — otherwise the wait can succeed on an element that is not visible yet")] bool include_offscreen = false)
    {
        var (parsedScope, windowTitle) = ParseTarget(scope, window);
        var info = await _uia.WaitForAsync(text, timeout_ms, interval_ms, ParseKind(kind), parsedScope, windowTitle, include_offscreen);
        return info is null ? "null" : JsonSerializer.Serialize(info);
    }
}

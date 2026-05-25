using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

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
        "any"          => FindKind.Any,
        "interactive"  => FindKind.Interactive,
        "text"         => FindKind.Text,
        "scrollable"   => FindKind.Scrollable,
        _ => throw new ArgumentException($"Unknown kind '{kind}'; expected any|interactive|text|scrollable")
    };

    [McpServerTool, Description("Return the full UI element tree of the foreground application.")]
    public async Task<string> GetState()
    {
        var tree = await _uia.GetStateAsync();
        return JsonSerializer.Serialize(tree);
    }

    [McpServerTool, Description("Find UI elements by text. kind: any, interactive, text, scrollable.")]
    public async Task<string> FindElement(
        [Description("Text to search for in element names/values")] string text,
        [Description("Element kind filter: any, interactive, text, scrollable")] string kind = "any")
    {
        var result = await _uia.FindElementAsync(text, ParseKind(kind));
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

    [McpServerTool, Description("Assert a UI element state. Returns 'PASS' or 'FAIL: <reason>'.")]
    public async Task<string> AssertElement(
        [Description("Element ID to check")] string element_id,
        [Description("State to assert: exists, enabled, checked, value, visible, focused")] string state)
    {
        bool pass = await _uia.AssertElementAsync(element_id, state);
        return pass ? "PASS" : $"FAIL: {state}";
    }

    [McpServerTool, Description("Interact with a UI element (click, toggle, select, focus, type).")]
    public async Task<string> InteractElement(
        [Description("Element ID to interact with")] string element_id,
        [Description("Action: click, toggle, select, focus, type")] string action,
        [Description("Value for 'type' or 'select' actions")] string? value = null)
    {
        await _uia.InteractAsync(element_id, action, value);
        return "interacted";
    }

    [McpServerTool, Description("Extract tabular data from a UI element via GridPattern.")]
    public async Task<string> GetTable(
        [Description("Element ID of a grid/table control")] string element_id)
    {
        var table = await _uia.GetTableAsync(element_id);
        return JsonSerializer.Serialize(table);
    }

    [McpServerTool, Description("Wait for a UI element matching text to appear. Returns element info or 'null' on timeout.")]
    public async Task<string> WaitFor(
        [Description("Text to wait for in element names/values")] string text,
        [Description("Timeout in milliseconds")] int timeout_ms = 10000,
        [Description("Poll interval in milliseconds")] int interval_ms = 500)
    {
        var info = await _uia.WaitForAsync(text, timeout_ms, interval_ms);
        return info is null ? "null" : JsonSerializer.Serialize(info);
    }
}

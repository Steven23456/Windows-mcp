using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class InputTools
{
    private readonly IInputService _input;
    private readonly IClipboardService _clipboard;

    public InputTools(IInputService input, IClipboardService clipboard)
    {
        _input = input;
        _clipboard = clipboard;
    }

    private static MouseButton ParseButton(string s) => s.ToLowerInvariant() switch
    {
        "left" or "l" => MouseButton.Left,
        "right" or "r" => MouseButton.Right,
        "middle" or "m" => MouseButton.Middle,
        _ => throw new ArgumentException($"Unknown button '{s}'; expected left|right|middle")
    };

    [McpServerTool, Description("Click at screen coordinates.")]
    public async Task<string> Click(
        [Description("X coordinate in pixels")] int x,
        [Description("Y coordinate in pixels")] int y,
        [Description("Mouse button: left, right, or middle")] string button = "left",
        [Description("Number of clicks (1=single, 2=double, 3=triple)")] int clicks = 1)
    {
        var result = await _input.ClickAsync(x, y, ParseButton(button), clicks);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Drag from one point to another.")]
    public async Task<string> Drag(int from_x, int from_y, int to_x, int to_y, string button = "left")
        => JsonSerializer.Serialize(await _input.DragAsync(from_x, from_y, to_x, to_y, ParseButton(button)));

    [McpServerTool, Description("Hover cursor at coordinates (or move cursor with duration_ms=0).")]
    public async Task<string> Hover(int x, int y, int duration_ms = 0)
    {
        await _input.HoverAsync(x, y, duration_ms);
        return $"hovered at ({x},{y}) for {duration_ms}ms";
    }

    [McpServerTool, Description("Type a string at the focused input.")]
    public async Task<string> Type([Description("Text to type")] string text)
        => JsonSerializer.Serialize(await _input.TypeAsync(text));

    [McpServerTool, Description("Press a single key by name (enter, tab, F1-F12, arrows, etc.)")]
    public async Task<string> Key([Description("Key name")] string key)
    {
        await _input.PressKeyAsync(key);
        return $"pressed {key}";
    }

    [McpServerTool, Description("Press a keyboard shortcut like ctrl+c, alt+tab, ctrl+shift+s.")]
    public async Task<string> Shortcut([Description("Shortcut, e.g. 'ctrl+c'")] string shortcut)
    {
        await _input.PressShortcutAsync(shortcut);
        return $"pressed {shortcut}";
    }

    [McpServerTool, Description("Scroll the mouse wheel at coordinates.")]
    public async Task<string> Scroll(int x, int y, [Description("up|down|left|right")] string direction, int amount = 3)
    {
        await _input.ScrollAsync(x, y, direction, amount);
        return $"scrolled {direction} by {amount} at ({x},{y})";
    }

    [McpServerTool, Description("Clipboard get/set.")]
    public async Task<string> Clipboard(
        [Description("Action: get or set")] string action,
        [Description("Text to set; ignored for 'get'")] string? text = null)
    {
        switch (action.ToLowerInvariant())
        {
            case "get":
                return await _clipboard.GetTextAsync() ?? "";
            case "set":
                if (text == null) throw new ArgumentException("'set' requires text parameter");
                await _clipboard.SetTextAsync(text);
                return $"set ({text.Length} chars)";
            default:
                throw new ArgumentException($"Unknown clipboard action '{action}'; expected get|set");
        }
    }
}

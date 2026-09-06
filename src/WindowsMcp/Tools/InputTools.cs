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

    [McpServerTool, Description("Click at screen coordinates. Coordinates are physical pixels on the virtual desktop: origin = top-left of the primary monitor, so monitors left of / above it have negative values (see multi_monitor for each monitor's bounds).")]
    public async Task<string> Click(
        [Description("X coordinate in physical pixels (virtual desktop)")] int x,
        [Description("Y coordinate in physical pixels (virtual desktop)")] int y,
        [Description("Mouse button: left, right, or middle")] string button = "left",
        [Description("Number of clicks (1=single, 2=double, 3=triple)")] int clicks = 1)
    {
        var result = await _input.ClickAsync(x, y, ParseButton(button), clicks);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool, Description("Drag from one point to another. Same coordinate space as click: physical virtual-desktop pixels, primary monitor's top-left = (0,0).")]
    public async Task<string> Drag(int from_x, int from_y, int to_x, int to_y, string button = "left")
        => JsonSerializer.Serialize(await _input.DragAsync(from_x, from_y, to_x, to_y, ParseButton(button)));

    [McpServerTool, Description("Move the cursor to coordinates and optionally hold it there (duration_ms). Same coordinate space as click: physical virtual-desktop pixels.")]
    public async Task<string> Hover(int x, int y, int duration_ms = 0)
    {
        await _input.HoverAsync(x, y, duration_ms);
        return $"hovered at ({x},{y}) for {duration_ms}ms";
    }

    [McpServerTool, Description("Type a string at the focused input.")]
    public async Task<string> Type([Description("Text to type")] string text)
        => JsonSerializer.Serialize(await _input.TypeAsync(text));

    [McpServerTool, Description("Press one key: a character (a, 7, /), f1-f24, or a name (enter, tab, esc, backspace, delete, up/down/left/right, home, end, pageup, pagedown, insert, win, printscreen). For chords use shortcut.")]
    public async Task<string> Key([Description("Key: a character, f1-f24, or a key name")] string key)
    {
        await _input.PressKeyAsync(key);
        return $"pressed {key}";
    }

    [McpServerTool, Description("Press a chord: ctrl+c, ctrl+shift+s, win+r, alt+f4, ctrl+1. A single key such as win (opens Start) also works. Join parts with '+'; write plus for the + key.")]
    public async Task<string> Shortcut([Description("Chord, e.g. 'ctrl+c' or 'win+r'; a bare key like 'win' is allowed")] string shortcut)
    {
        await _input.PressShortcutAsync(shortcut);
        return $"pressed {shortcut}";
    }

    [McpServerTool, Description("Scroll the mouse wheel at coordinates. Same coordinate space as click: physical virtual-desktop pixels.")]
    public async Task<string> Scroll(int x, int y, [Description("up|down|left|right")] string direction, int amount = 3)
    {
        await _input.ScrollAsync(x, y, direction, amount);
        return $"scrolled {direction} by {amount} at ({x},{y})";
    }

    [McpServerTool(ReadOnly = true, Idempotent = true), Description("Pause for a number of seconds (more than 0, at most 60) and return; use it between an action and the next snapshot instead of a PowerShell sleep. Returns {\"waited\": seconds}. For a longer pause, or to wait until something is on screen, use wait_for.")]
    public async Task<string> Wait(
        [Description("Seconds to pause: more than 0 and at most 60 (fractions allowed)")] double seconds,
        CancellationToken ct = default)
    {
        // NaN fails the first comparison, infinity the second: nothing outside (0, 60] gets a delay.
        if (!(seconds > 0 && seconds <= 60))
            throw new ArgumentException($"seconds must be more than 0 and at most 60, got {seconds}; for a longer or conditional wait use wait_for.", nameof(seconds));

        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        return JsonSerializer.Serialize(new { waited = seconds });
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

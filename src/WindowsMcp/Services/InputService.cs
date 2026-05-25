using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsInput;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

// Disambiguate: H.InputSimulator also exposes a WindowsInput.MouseButton enum.
using MouseButton = WindowsMcp.Abstractions.Models.MouseButton;

namespace WindowsMcp.Services;

public sealed class InputService : IInputService
{
    private readonly InputSimulator _sim = new();

    private static readonly Dictionary<string, VirtualKeyCode> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"]     = VirtualKeyCode.RETURN,
        ["tab"]       = VirtualKeyCode.TAB,
        ["esc"]       = VirtualKeyCode.ESCAPE,
        ["escape"]    = VirtualKeyCode.ESCAPE,
        ["space"]     = VirtualKeyCode.SPACE,
        ["backspace"] = VirtualKeyCode.BACK,
        ["delete"]    = VirtualKeyCode.DELETE,
        ["up"]        = VirtualKeyCode.UP,
        ["down"]      = VirtualKeyCode.DOWN,
        ["left"]      = VirtualKeyCode.LEFT,
        ["right"]     = VirtualKeyCode.RIGHT,
        ["home"]      = VirtualKeyCode.HOME,
        ["end"]       = VirtualKeyCode.END,
        ["pageup"]    = VirtualKeyCode.PRIOR,
        ["pagedown"]  = VirtualKeyCode.NEXT,
        ["ctrl"]      = VirtualKeyCode.CONTROL,
        ["alt"]       = VirtualKeyCode.MENU,
        ["shift"]     = VirtualKeyCode.SHIFT,
        ["win"]       = VirtualKeyCode.LWIN,
    };

    static InputService()
    {
        for (int i = 1; i <= 12; i++)
            KeyMap[$"f{i}"] = (VirtualKeyCode)((int)VirtualKeyCode.F1 + i - 1);
    }

    // Use CsWin32 GetSystemMetrics instead of System.Windows.Forms.Screen to avoid
    // adding a WinForms dependency to a console/stdio server.
    private static int ScreenWidth  => PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
    private static int ScreenHeight => PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);

    private void MoveCursorToVirtualDesktop(int x, int y)
    {
        _sim.Mouse.MoveMouseToPositionOnVirtualDesktop(
            x * (65535.0 / ScreenWidth),
            y * (65535.0 / ScreenHeight));
    }

    public Task<ClickResult> ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MoveCursorToVirtualDesktop(x, y);
        for (int i = 0; i < clicks; i++)
        {
            ct.ThrowIfCancellationRequested();
            switch (button)
            {
                case MouseButton.Left:   _sim.Mouse.LeftButtonClick();   break;
                case MouseButton.Right:  _sim.Mouse.RightButtonClick();  break;
                case MouseButton.Middle: _sim.Mouse.MiddleButtonClick(); break;
            }
        }
        return Task.FromResult(new ClickResult(x, y, button, clicks));
    }

    public Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // H.InputSimulator exposes only Left/Right ButtonDown/Up; no MiddleButtonDown/Up.
        // Reject middle-button drag rather than silently degrading to a left-button drag.
        if (button == MouseButton.Middle)
            throw new NotSupportedException("Middle-button drag is not supported by H.InputSimulator");

        MoveCursorToVirtualDesktop(fromX, fromY);

        switch (button)
        {
            case MouseButton.Left:  _sim.Mouse.LeftButtonDown();  break;
            case MouseButton.Right: _sim.Mouse.RightButtonDown(); break;
        }

        MoveCursorToVirtualDesktop(toX, toY);

        switch (button)
        {
            case MouseButton.Left:  _sim.Mouse.LeftButtonUp();  break;
            case MouseButton.Right: _sim.Mouse.RightButtonUp(); break;
        }

        return Task.FromResult(new DragResult(fromX, fromY, toX, toY, button));
    }

    public Task HoverAsync(int x, int y, int durationMs = 0, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MoveCursorToVirtualDesktop(x, y);
        if (durationMs > 0) return Task.Delay(durationMs, ct);
        return Task.CompletedTask;
    }

    public Task<TypeResult> TypeAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _sim.Keyboard.TextEntry(text);
        return Task.FromResult(new TypeResult(text.Length));
    }

    public Task PressKeyAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!KeyMap.TryGetValue(key, out var vk))
            throw new ArgumentException($"Unknown key: '{key}'", nameof(key));
        _sim.Keyboard.KeyPress(vk);
        return Task.CompletedTask;
    }

    public Task PressShortcutAsync(string shortcut, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException($"Invalid shortcut format: '{shortcut}'", nameof(shortcut));

        var vks = new List<VirtualKeyCode>();
        foreach (var part in parts)
        {
            if (!KeyMap.TryGetValue(part, out var vk))
                throw new ArgumentException($"Unknown key in shortcut: '{part}'", nameof(shortcut));
            vks.Add(vk);
        }

        var mods = vks.Take(vks.Count - 1).ToArray();
        var final = vks[^1];
        _sim.Keyboard.ModifiedKeyStroke(mods, final);
        return Task.CompletedTask;
    }

    public async Task ScrollAsync(int x, int y, string direction, int amount = 3, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await HoverAsync(x, y, 0, ct);
        switch (direction.ToLowerInvariant())
        {
            case "up":    _sim.Mouse.VerticalScroll(amount);    break;
            case "down":  _sim.Mouse.VerticalScroll(-amount);   break;
            case "left":  _sim.Mouse.HorizontalScroll(-amount); break;
            case "right": _sim.Mouse.HorizontalScroll(amount);  break;
            default: throw new ArgumentException($"Invalid direction: '{direction}'", nameof(direction));
        }
    }
}

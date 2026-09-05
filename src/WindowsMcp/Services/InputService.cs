using System.Runtime.InteropServices;
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

    /// <summary>
    /// Places the cursor at (<paramref name="x"/>, <paramref name="y"/>): physical pixels on the
    /// virtual desktop, origin at the primary monitor's top-left, so monitors left of / above it
    /// have negative coordinates. The button and wheel events sent afterwards carry no position of
    /// their own (H.InputSimulator's LeftButtonClick etc.), so they act wherever this put the cursor.
    /// </summary>
    /// <remarks>
    /// SetCursorPos rather than an absolute SendInput move (D-3): the process is Per-Monitor-V2 DPI
    /// aware (Program.cs), so this, UIA BoundingRectangle and multi_monitor share one physical-pixel
    /// space, and there is no 0..65535 normalisation to get wrong — the previous code scaled by the
    /// primary monitor's size, so every secondary-monitor click landed somewhere else.
    /// SetCursorPos silently clamps a point outside the virtual screen to the nearest edge, which is
    /// that same failure in a different coat, so the position is read back and a mismatch throws.
    /// </remarks>
    private static void MoveCursor(int x, int y)
    {
        if (!PInvoke.SetCursorPos(x, y))
            throw new InvalidOperationException($"SetCursorPos({x},{y}) failed (Win32 error {Marshal.GetLastPInvokeError()}).");

        if (!PInvoke.GetCursorPos(out var actual))
            throw new InvalidOperationException($"GetCursorPos failed (Win32 error {Marshal.GetLastPInvokeError()}).");

        if (actual.X == x && actual.Y == y) return;

        int left   = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top    = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width  = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        throw new ArgumentOutOfRangeException(nameof(x),
            $"({x},{y}) is not on any monitor: the cursor landed at ({actual.X},{actual.Y}). The virtual screen spans " +
            $"x {left}..{left + width - 1}, y {top}..{top + height - 1} in physical pixels with the origin at the " +
            "primary monitor's top-left; see multi_monitor for each monitor's bounds.");
    }

    public Task<ClickResult> ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        MoveCursor(x, y);
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

        MoveCursor(fromX, fromY);

        switch (button)
        {
            case MouseButton.Left:  _sim.Mouse.LeftButtonDown();  break;
            case MouseButton.Right: _sim.Mouse.RightButtonDown(); break;
        }

        MoveCursor(toX, toY);

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
        MoveCursor(x, y);
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
        var token = ShortcutParser.ResolveKey(key);
        if (token.ImpliedModifiers.Length == 0)
            _sim.Keyboard.KeyPress(token.Key);
        else
            _sim.Keyboard.ModifiedKeyStroke(token.ImpliedModifiers, token.Key);   // e.g. key("+") on a US layout = Shift + OEM_PLUS
        return Task.CompletedTask;
    }

    public Task PressShortcutAsync(string shortcut, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var chord = ShortcutParser.Parse(shortcut);
        if (chord.Modifiers.Length == 0)
            _sim.Keyboard.KeyPress(chord.Key);                                     // bare key: "win" opens Start, "esc" dismisses
        else
            _sim.Keyboard.ModifiedKeyStroke(chord.Modifiers, chord.Key);
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

    /// <summary>Where the cursor is, in the same virtual-desktop pixels <see cref="MoveCursor"/> accepts (A-11).</summary>
    public Task<CursorPosition> GetCursorPositionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!PInvoke.GetCursorPos(out var p))
            throw new InvalidOperationException($"GetCursorPos failed (Win32 error {Marshal.GetLastPInvokeError()}).");
        return Task.FromResult(new CursorPosition(p.X, p.Y));
    }
}

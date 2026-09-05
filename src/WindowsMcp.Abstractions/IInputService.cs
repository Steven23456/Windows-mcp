using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IInputService
{
    Task<ClickResult> ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default);
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task HoverAsync(int x, int y, int durationMs = 0, CancellationToken ct = default);
    Task<TypeResult> TypeAsync(string text, CancellationToken ct = default);
    Task PressKeyAsync(string key, CancellationToken ct = default);
    Task PressShortcutAsync(string shortcut, CancellationToken ct = default);
    Task ScrollAsync(int x, int y, string direction, int amount = 3, CancellationToken ct = default);

    /// <summary>The live cursor position in virtual-desktop pixels (A-11).</summary>
    Task<CursorPosition> GetCursorPositionAsync(CancellationToken ct = default);
}

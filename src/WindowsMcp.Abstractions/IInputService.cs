using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IInputService
{
    Task<ClickResult> ClickAsync(int x, int y, MouseButton button = MouseButton.Left, int clicks = 1, CancellationToken ct = default);
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button = MouseButton.Left, CancellationToken ct = default);

    /// <summary>
    /// B-2: a drag the target actually recognises — press, an initial nudge past
    /// <c>SM_CXDRAG</c>, <paramref name="steps"/> interpolated moves spaced
    /// <paramref name="durationMs"/>/<paramref name="steps"/> apart, release. The overload above
    /// keeps its press-jump-release behaviour for callers that want today's cheap drag.
    /// </summary>
    Task<DragResult> DragAsync(int fromX, int fromY, int toX, int toY, MouseButton button, int durationMs, int steps, CancellationToken ct = default);

    Task HoverAsync(int x, int y, int durationMs = 0, CancellationToken ct = default);
    Task<TypeResult> TypeAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// B-1 (roadmap C8): clear / caret / press-enter and the keys-vs-paste decision, planned by
    /// <c>TypePlanner</c> and executed here. The single-argument overload above stays and means
    /// exactly this one with <c>default</c> options — D-2's <c>interact_element(type)</c> keyboard
    /// fallback still calls it.
    /// </summary>
    Task<TypeResult> TypeAsync(string text, TypeOptions options, CancellationToken ct = default);

    Task PressKeyAsync(string key, CancellationToken ct = default);
    Task PressShortcutAsync(string shortcut, CancellationToken ct = default);
    Task ScrollAsync(int x, int y, string direction, int amount = 3, CancellationToken ct = default);

    /// <summary>
    /// B-3: <paramref name="shiftWheel"/> holds Shift and sends the VERTICAL wheel, which is how a
    /// window with no horizontal wheel scrolls sideways. Only meaningful for left/right.
    /// </summary>
    Task ScrollAsync(int x, int y, string direction, int amount, bool shiftWheel, CancellationToken ct = default);

    /// <summary>
    /// B-7: hold a key down until <see cref="KeyUpAsync"/> releases it — the modifier half of
    /// <c>multi_select</c>'s Ctrl+click batch. The key name is the same vocabulary
    /// <see cref="PressKeyAsync"/> takes (<c>ctrl</c>, <c>shift</c>, <c>alt</c>, a character).
    /// </summary>
    Task KeyDownAsync(string key, CancellationToken ct = default);

    /// <summary>B-7: release a key held by <see cref="KeyDownAsync"/>. Always called in a finally.</summary>
    Task KeyUpAsync(string key, CancellationToken ct = default);

    /// <summary>The live cursor position in virtual-desktop pixels (A-11).</summary>
    Task<CursorPosition> GetCursorPositionAsync(CancellationToken ct = default);
}

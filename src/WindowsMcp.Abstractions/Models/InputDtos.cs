namespace WindowsMcp.Abstractions.Models;

public enum MouseButton { Left, Right, Middle }

public record ClickResult(int X, int Y, MouseButton Button, int Clicks);
public record DragResult(int FromX, int FromY, int ToX, int ToY, MouseButton Button);
public record TypeResult(int CharsTyped);

/// <summary>
/// Where the mouse cursor is, in virtual-desktop pixels — the same signed coordinate space
/// <c>click</c>/<c>drag</c>/<c>scroll</c> and the screenshot metadata use (roadmap C1), so a
/// monitor left of or above the primary gives negative values.
/// </summary>
public record CursorPosition(int X, int Y);

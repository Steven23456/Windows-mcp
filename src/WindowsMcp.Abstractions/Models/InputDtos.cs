namespace WindowsMcp.Abstractions.Models;

public enum MouseButton { Left, Right, Middle }

public record ClickResult(int X, int Y, MouseButton Button, int Clicks);
public record DragResult(int FromX, int FromY, int ToX, int ToY, MouseButton Button);
public record TypeResult(int CharsTyped);

namespace WindowsMcp.Abstractions.Models;

public enum MouseButton { Left, Right, Middle }

public record ClickResult(int X, int Y, MouseButton Button, int Clicks);
public record DragResult(int FromX, int FromY, int ToX, int ToY, MouseButton Button);

/// <param name="Method">
/// B-1 (roadmap C8): how the text was entered — <c>keys</c> (per-key injection, the only mode
/// before B-1) or <c>paste</c> (clipboard + Ctrl+V). Defaulted so every pre-B-1 construction site
/// keeps compiling and keeps reporting <c>keys</c>.
/// </param>
/// <param name="ClipboardRestored">
/// B-1: only meaningful for <c>paste</c>. True when the clipboard held text and that text was put
/// back afterwards; false when it held something that is not text, so nothing was restored; null
/// when no paste happened.
/// </param>
public record TypeResult(int CharsTyped, string Method = "keys", bool? ClipboardRestored = null);

/// <summary>B-1: where the caret goes before the text is entered.</summary>
public enum CaretPosition { Idle, Start, End }

/// <param name="Clear">Select all and delete before typing (Ctrl+A, Backspace).</param>
/// <param name="Caret">Move the caret to the start / end of the field first; Idle moves nothing.</param>
/// <param name="PressEnter">Press Enter after the text.</param>
/// <param name="PaceMs">Delay between keys-mode chunks and keys; 0 is allowed, negative is not.</param>
public record TypeOptions(
    bool Clear = false,
    CaretPosition Caret = CaretPosition.Idle,
    bool PressEnter = false,
    int PaceMs = 5);

/// <summary>
/// Where the mouse cursor is, in virtual-desktop pixels — the same signed coordinate space
/// <c>click</c>/<c>drag</c>/<c>scroll</c> and the screenshot metadata use (roadmap C1), so a
/// monitor left of or above the primary gives negative values.
/// </summary>
public record CursorPosition(int X, int Y);

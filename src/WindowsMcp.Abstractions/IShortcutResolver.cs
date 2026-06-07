namespace WindowsMcp.Abstractions;

/// <summary>
/// Resolves a Windows <c>.lnk</c> shortcut to the executable it launches. Non-shortcut
/// paths are returned unchanged; unresolvable shortcuts yield null.
/// </summary>
public interface IShortcutResolver
{
    string? ResolveTarget(string shortcutPath);
}

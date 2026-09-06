namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// The outcome of a toast: whether Windows accepted it, the AppUserModelId it was shown under,
/// whether that id is one the platform knows (a packaged id, or one with an
/// <c>AppUserModelId</c> registration), and why the toast was dropped when it was.
/// </summary>
public record NotificationResult(bool Shown, string AppId, bool Registered, string? Note);

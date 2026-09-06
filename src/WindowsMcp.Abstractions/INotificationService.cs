using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface INotificationService
{
    /// <summary>
    /// Show a toast under an AppUserModelId (null or omitted = the server's own default id,
    /// which the service registers under HKCU once per process).
    /// </summary>
    Task<NotificationResult> ShowAsync(string title, string message, string? appId = null, CancellationToken ct = default);
}

namespace WindowsMcp.Abstractions;

public interface INotificationService
{
    Task ShowAsync(string title, string message, CancellationToken ct = default);
}

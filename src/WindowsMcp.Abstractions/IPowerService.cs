namespace WindowsMcp.Abstractions;

public interface IPowerService
{
    Task ExecuteAsync(string action, CancellationToken ct = default);
}

using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IPowerShellService : IDisposable
{
    Task<PSResult> RunAsync(string command, CancellationToken ct = default);
}

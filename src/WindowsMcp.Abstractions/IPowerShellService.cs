using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IPowerShellService : IDisposable
{
    Task<PSResult> RunAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// C-6: run <paramref name="command"/> under a per-call clock. <paramref name="timeout"/> null
    /// is the execution backstop only (what the overload above does); a non-positive value is an
    /// <see cref="ArgumentException"/>. The timer starts AFTER the serialization gate is acquired,
    /// so a queued caller does not burn its budget waiting. Expiry does not throw: the child tree
    /// is killed and the partial output comes back as <see cref="PSResult"/> with
    /// <see cref="PSResult.TimedOut"/> true, <c>Success:false</c>, <c>ExitCode:-1</c> and
    /// <c>Errors: ["timed out after Ns"]</c>. Only <paramref name="ct"/> still throws.
    /// </summary>
    Task<PSResult> RunAsync(string command, TimeSpan? timeout, CancellationToken ct = default);
}

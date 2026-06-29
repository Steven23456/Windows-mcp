using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IReliabilityService
{
    /// <summary>Crash minidumps plus recent reliability failure records (capped at <paramref name="maxRecords"/>).</summary>
    Task<ReliabilityReport> GetAsync(int maxRecords = 50, CancellationToken ct = default);
}

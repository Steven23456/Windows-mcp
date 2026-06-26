using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IStorageService
{
    /// <summary>
    /// Diagnose disk/volume health from the storage stack (metadata-first, hang-safe).
    /// </summary>
    /// <param name="driveLetter">Limit the volumes section to a single drive letter (e.g. "F" or "F:"). Null = all.</param>
    /// <param name="includeUsage">Also probe free space per volume (time-boxed). False = metadata only.</param>
    /// <param name="timeoutSeconds">Overall budget; the underlying query is aborted past it. Clamped to 5-300.</param>
    Task<StorageHealthReport> GetHealthAsync(
        string? driveLetter = null,
        bool includeUsage = false,
        int timeoutSeconds = 30,
        CancellationToken ct = default);
}

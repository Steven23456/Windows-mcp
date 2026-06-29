using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IDiskService
{
    /// <summary>Top-level directories under <paramref name="root"/> by total size (descending, top 10).</summary>
    Task<DiskUsageEntry[]> GetUsageAsync(string root, CancellationToken ct = default);

    /// <summary>File extensions under <paramref name="root"/> by total size (descending, top 10).</summary>
    Task<FileTypeEntry[]> GetFileTypesAsync(string root, CancellationToken ct = default);

    /// <summary>Files under <paramref name="root"/> not modified in the last <paramref name="olderThanDays"/> days.</summary>
    Task<StaleFileEntry[]> GetStaleAsync(string root, int olderThanDays = 365, CancellationToken ct = default);

    /// <summary>Reclaimable space: temp, INetCache, and recycle bin sizes.</summary>
    Task<ReclaimableSpace> GetReclaimableAsync(CancellationToken ct = default);
}

using System.Text.Json;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class DiskService : IDiskService
{
    private const int TopN = 10;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IFileSystemService _fs;
    private readonly IPowerShellService _ps;

    public DiskService(IFileSystemService fs, IPowerShellService ps)
    {
        _fs = fs;
        _ps = ps;
    }

    public async Task<DiskUsageEntry[]> GetUsageAsync(string root, CancellationToken ct = default)
    {
        var hits = await _fs.SearchAsync(root, "*", null, null, false, ct);
        return hits
            .GroupBy(h => GetTopLevelDir(root, h.Path))
            .Select(g =>
            {
                var sum = g.Sum(h => h.Size);
                return new DiskUsageEntry(g.Key, sum, FormatBytes(sum));
            })
            .OrderByDescending(e => e.SizeBytes)
            .Take(TopN)
            .ToArray();
    }

    public async Task<FileTypeEntry[]> GetFileTypesAsync(string root, CancellationToken ct = default)
    {
        var hits = await _fs.SearchAsync(root, "*", null, null, false, ct);
        return hits
            .GroupBy(h => Path.GetExtension(h.Path).ToLowerInvariant())
            .Select(g =>
            {
                var sum = g.Sum(h => h.Size);
                return new FileTypeEntry(string.IsNullOrEmpty(g.Key) ? "(none)" : g.Key, g.Count(), sum, FormatBytes(sum));
            })
            .OrderByDescending(e => e.SizeBytes)
            .Take(TopN)
            .ToArray();
    }

    public async Task<StaleFileEntry[]> GetStaleAsync(string root, int olderThanDays = 365, CancellationToken ct = default)
    {
        var threshold = DateTime.UtcNow.AddDays(-olderThanDays);
        // SearchAsync returns files where Modified >= since; we want the inverse, so fetch all and filter.
        var all = await _fs.SearchAsync(root, "*", null, null, false, ct);
        return all
            .Where(h => h.Modified < threshold)
            .OrderBy(h => h.Modified)
            .Select(h => new StaleFileEntry(h.Path, h.Size, FormatBytes(h.Size), h.Modified))
            .ToArray();
    }

    public async Task<ReclaimableSpace> GetReclaimableAsync(CancellationToken ct = default)
    {
        var result = await _ps.RunAsync(ReclaimableScript, ct);

        // Guard against the silent empty-output failure mode (see storage_health): surface it
        // instead of returning a zeroed/blank result that reads as "nothing reclaimable".
        if (string.IsNullOrWhiteSpace(result.Stdout))
            throw new InvalidOperationException(
                $"reclaimable-space query returned no output (exit {result.ExitCode}). Stderr: {result.Stderr}");

        return JsonSerializer.Deserialize<ReclaimableSpace>(result.Stdout, JsonOpts)
            ?? throw new InvalidOperationException("reclaimable-space query returned unparseable output.");
    }

    internal static string GetTopLevelDir(string root, string filePath)
    {
        var rel = Path.GetRelativePath(root, filePath);
        var parts = rel.Split(Path.DirectorySeparatorChar, 2);
        return parts.Length > 1 ? Path.Combine(root, parts[0]) : root;
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576L)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024L)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }

    // PowerShell 5.1-safe: no `??` (PS7-only). [long]$null evaluates to 0, and $null is treated
    // as 0 in arithmetic, so empty/missing folders contribute 0 without null-coalescing.
    private const string ReclaimableScript = @"
$tempSize = (Get-ChildItem -Path $env:TEMP -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
$inetCacheSize = (Get-ChildItem -Path ""$env:LOCALAPPDATA\Microsoft\Windows\INetCache"" -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
$shell = New-Object -ComObject Shell.Application
$recycleBin = $shell.Namespace(0xA)
$recycleBinSize = ($recycleBin.Items() | Measure-Object -Property Size -Sum).Sum
[PSCustomObject]@{
    TempBytes       = [long]$tempSize
    InetCacheBytes  = [long]$inetCacheSize
    RecycleBinBytes = [long]$recycleBinSize
    TotalBytes      = [long]$tempSize + [long]$inetCacheSize + [long]$recycleBinSize
} | ConvertTo-Json";
}

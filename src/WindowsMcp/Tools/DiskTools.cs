using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class DiskTools
{
    private readonly IFileSystemService _fs;
    private readonly IPowerShellService _ps;

    public DiskTools(IFileSystemService fs, IPowerShellService ps)
    {
        _fs = fs;
        _ps = ps;
    }

    [McpServerTool, Description("Inspect disk usage. mode: usage (top dirs by size), reclaimable (temp/cache/recyclebin), file_types (group by extension), stale (files older than 365 days). path: root to analyse.")]
    public async Task<string> DiskInspect(
        [Description("Mode: usage, reclaimable, file_types, stale")] string mode,
        [Description("Root path to inspect (not required for reclaimable)")] string? path = null)
    {
        switch (mode.ToLowerInvariant())
        {
            case "usage":
            {
                var root = path ?? @"C:\";
                var hits = await _fs.SearchAsync(root, "*", null, null, false);
                // Group by top-level subdirectory under root, sum sizes, sort desc, take top 10
                var groups = hits
                    .GroupBy(h => GetTopLevelDir(root, h.Path))
                    .Select(g => new
                    {
                        dir = g.Key,
                        size_bytes = g.Sum(h => h.Size),
                        size_human = FormatBytes(g.Sum(h => h.Size))
                    })
                    .OrderByDescending(g => g.size_bytes)
                    .Take(10)
                    .ToArray();
                return JsonSerializer.Serialize(groups);
            }

            case "reclaimable":
            {
                var script = @"
$tempSize = (Get-ChildItem -Path $env:TEMP -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum ?? 0
$inetCacheSize = (Get-ChildItem -Path ""$env:LOCALAPPDATA\Microsoft\Windows\INetCache"" -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum ?? 0
$shell = New-Object -ComObject Shell.Application
$recycleBin = $shell.Namespace(0xA)
$recycleBinSize = ($recycleBin.Items() | Measure-Object -Property Size -Sum).Sum ?? 0
[PSCustomObject]@{
    temp_bytes        = [long]$tempSize
    inet_cache_bytes  = [long]$inetCacheSize
    recycle_bin_bytes = [long]$recycleBinSize
    total_bytes       = [long]($tempSize + $inetCacheSize + $recycleBinSize)
} | ConvertTo-Json";
                var result = await _ps.RunAsync(script);
                return result.Stdout;
            }

            case "file_types":
            {
                var root = path ?? @"C:\";
                var hits = await _fs.SearchAsync(root, "*", null, null, false);
                var groups = hits
                    .GroupBy(h => Path.GetExtension(h.Path).ToLowerInvariant())
                    .Select(g => new
                    {
                        extension = string.IsNullOrEmpty(g.Key) ? "(none)" : g.Key,
                        count = g.Count(),
                        size_bytes = g.Sum(h => h.Size),
                        size_human = FormatBytes(g.Sum(h => h.Size))
                    })
                    .OrderByDescending(g => g.size_bytes)
                    .Take(10)
                    .ToArray();
                return JsonSerializer.Serialize(groups);
            }

            case "stale":
            {
                var root = path ?? @"C:\";
                var threshold = DateTime.UtcNow.AddDays(-365);
                // SearchAsync returns files where LastWriteTimeUtc >= modifiedSince;
                // we want the inverse (files older than threshold), so fetch all and post-filter.
                var all = await _fs.SearchAsync(root, "*", null, null, false);
                var stale = all
                    .Where(h => h.Modified < threshold)
                    .OrderBy(h => h.Modified)
                    .Select(h => new
                    {
                        path = h.Path,
                        size_bytes = h.Size,
                        size_human = FormatBytes(h.Size),
                        modified = h.Modified
                    })
                    .ToArray();
                return JsonSerializer.Serialize(stale);
            }

            default:
                throw new ArgumentException($"Unknown mode '{mode}'; expected usage|reclaimable|file_types|stale");
        }
    }

    private static string GetTopLevelDir(string root, string filePath)
    {
        var rel = Path.GetRelativePath(root, filePath);
        var parts = rel.Split(Path.DirectorySeparatorChar, 2);
        return parts.Length > 1 ? Path.Combine(root, parts[0]) : root;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576L)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024L)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }
}

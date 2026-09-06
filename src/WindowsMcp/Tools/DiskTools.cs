using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed class DiskTools
{
    private const string DefaultRoot = @"C:\";
    private readonly IDiskService _disk;

    public DiskTools(IDiskService disk) => _disk = disk;

    [McpServerTool(Title = "Inspect disk", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Inspect disk usage. mode: usage (top dirs by size), reclaimable (temp/cache/recyclebin), file_types (group by extension), stale (files older than 365 days). path: root to analyse.")]
    public async Task<string> DiskInspect(
        [Description("Mode: usage, reclaimable, file_types, stale")] string mode,
        [Description("Root path to inspect (not required for reclaimable)")] string? path = null,
        CancellationToken ct = default)
    {
        // Serialize the concrete result type per branch — JsonSerializer.Serialize(object)
        // would emit {} because it serializes against the compile-time type.
        return mode.ToLowerInvariant() switch
        {
            "usage" => JsonSerializer.Serialize(await _disk.GetUsageAsync(path ?? DefaultRoot, ct)),
            "reclaimable" => JsonSerializer.Serialize(await _disk.GetReclaimableAsync(ct)),
            "file_types" => JsonSerializer.Serialize(await _disk.GetFileTypesAsync(path ?? DefaultRoot, ct)),
            "stale" => JsonSerializer.Serialize(await _disk.GetStaleAsync(path ?? DefaultRoot, 365, ct)),
            _ => throw new ArgumentException($"Unknown mode '{mode}'; expected usage|reclaimable|file_types|stale")
        };
    }
}

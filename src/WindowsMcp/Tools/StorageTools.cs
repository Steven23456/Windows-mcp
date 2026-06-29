using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

[McpServerToolType]
public sealed partial class StorageTools
{
    private readonly IStorageService _storage;

    public StorageTools(IStorageService storage) => _storage = storage;

    [GeneratedRegex("^[A-Za-z]:?$")]
    private static partial Regex DriveLetterRegex();

    [McpServerTool, Description(
        "Diagnose drive/disk HEALTH (not usage). DEFAULT is fast and never wakes sleeping drives: each disk's " +
        "model, bus type, health, and online/offline (Get-Disk); the volume->disk/partition map (filesystem, " +
        "label, health); and recent disk-stack Error/Warning events. Set include_usage:true to ALSO collect " +
        "physical-disk SMART reliability counters (temperature, power-on-hours, uncorrected read/write errors) " +
        "AND per-volume free space — slower, because it wakes sleeping/USB drives (bounded by timeout_seconds). " +
        "drive_letter limits the volumes section to one drive (e.g. 'F'). For disk space/usage analysis use " +
        "disk_inspect instead.")]
    public async Task<string> StorageHealth(
        [Description("Limit the volumes section to one drive letter, e.g. 'F' or 'F:'. Omit for all drives.")] string? drive_letter = null,
        [Description("Add physical-disk SMART reliability + per-volume free space. Slower: wakes sleeping/USB drives. Default false = fast storage-stack metadata only.")] bool include_usage = false,
        [Description("Overall timeout in seconds (clamped 5-300, default 45).")] int timeout_seconds = 45,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(drive_letter) && !DriveLetterRegex().IsMatch(drive_letter.Trim()))
            throw new ArgumentException($"Invalid drive_letter '{drive_letter}'; expected a single letter A-Z, optionally with ':'.");

        var report = await _storage.GetHealthAsync(drive_letter, include_usage, timeout_seconds, ct);
        return JsonSerializer.Serialize(report);
    }
}

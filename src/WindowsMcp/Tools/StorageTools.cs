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
        "Diagnose drive/disk HEALTH (not usage). Returns physical disks (model, bus type, media, " +
        "SMART HealthStatus + reliability counters like temperature/power-on-hours/uncorrected errors), " +
        "each disk's online/offline + health, the volume->disk/partition map (filesystem, label, health), " +
        "and recent disk-stack Error/Warning events. Metadata-only and HANG-SAFE by default: free space is " +
        "NOT probed unless include_usage:true (each probe is time-boxed, so a sleeping/unresponsive drive is " +
        "flagged instead of stalling). drive_letter limits the volumes section to one drive (e.g. 'F'). " +
        "For disk space/usage analysis use disk_inspect instead.")]
    public async Task<string> StorageHealth(
        [Description("Limit the volumes section to one drive letter, e.g. 'F' or 'F:'. Omit for all drives.")] string? drive_letter = null,
        [Description("Also probe free space per volume (time-boxed). Default false = metadata only, never hangs.")] bool include_usage = false,
        [Description("Overall timeout in seconds (clamped 5-300, default 30).")] int timeout_seconds = 30,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(drive_letter) && !DriveLetterRegex().IsMatch(drive_letter.Trim()))
            throw new ArgumentException($"Invalid drive_letter '{drive_letter}'; expected a single letter A-Z, optionally with ':'.");

        var report = await _storage.GetHealthAsync(drive_letter, include_usage, timeout_seconds, ct);
        return JsonSerializer.Serialize(report);
    }
}

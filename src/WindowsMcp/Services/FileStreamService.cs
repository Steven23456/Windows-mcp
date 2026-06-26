using System.Collections.Generic;
using System.Text.Json;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class FileStreamService : IFileStreamService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IPowerShellService _ps;

    public FileStreamService(IPowerShellService ps) => _ps = ps;

    public async Task<FileStreamsDto> GetStreamsAsync(string path, CancellationToken ct = default)
    {
        // Reparse (symlink/junction) target via native .NET — null for a normal file/dir.
        string? linkTarget = null;
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            linkTarget = info.LinkTarget;
        }
        catch { /* path may not exist; the stream query below also no-ops */ }

        // ADS via Get-Item -Stream * (single pipeline, survives the -Command - stdin path).
        var esc = path.Replace("'", "''");
        var script = $"Get-Item -LiteralPath '{esc}' -Stream * -ErrorAction SilentlyContinue | " +
                     "Select-Object Stream,@{n='Length';e={[long]$_.Length}} | ConvertTo-Json";
        var result = await _ps.RunAsync(script, ct);

        return new FileStreamsDto(path, linkTarget, ParseStreams(result.Stdout));
    }

    internal static AlternateStreamInfo[] ParseStreams(string? stdout)
    {
        var json = stdout?.Trim();
        if (string.IsNullOrEmpty(json)) return Array.Empty<AlternateStreamInfo>();

        var raw = new List<RawStream>();
        if (json[0] == '[')
            raw.AddRange(JsonSerializer.Deserialize<RawStream[]>(json, JsonOpts) ?? Array.Empty<RawStream>());
        else if (JsonSerializer.Deserialize<RawStream>(json, JsonOpts) is { } one)
            raw.Add(one);

        // The default unnamed data stream is ":$DATA" — report only *alternate* streams.
        return raw
            .Where(s => !string.IsNullOrEmpty(s.Stream) && s.Stream != ":$DATA")
            .Select(s => new AlternateStreamInfo(s.Stream!, s.Length))
            .ToArray();
    }

    private sealed record RawStream(string? Stream, long Length);
}

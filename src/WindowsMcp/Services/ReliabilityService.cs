using System.Collections.Generic;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ReliabilityService : IReliabilityService
{
    private const string MinidumpDir = @"C:\Windows\Minidump";
    private readonly IWmiService _wmi;

    public ReliabilityService(IWmiService wmi) => _wmi = wmi;

    public async Task<ReliabilityReport> GetAsync(int maxRecords = 50, CancellationToken ct = default)
    {
        // Crash minidumps (native, fast).
        var dumps = new List<MinidumpInfo>();
        try
        {
            if (Directory.Exists(MinidumpDir))
            {
                foreach (var f in Directory.GetFiles(MinidumpDir, "*.dmp"))
                {
                    var fi = new FileInfo(f);
                    dumps.Add(new MinidumpInfo(fi.Name, fi.Length, fi.LastWriteTimeUtc));
                }
            }
        }
        catch { /* directory may be inaccessible; report no dumps */ }

        // Recent reliability failure records (WMI; best-effort).
        var records = new List<ReliabilityRecord>();
        string? note = null;
        try
        {
            var rows = await _wmi.QueryAsync("Win32_ReliabilityRecords", null, null, ct);
            records = rows.OfType<IDictionary<string, object>>()
                .Select(d => new ReliabilityRecord(
                    d.TryGetValue("SourceName", out var s) ? s?.ToString() : null,
                    d.TryGetValue("Message", out var m) ? m?.ToString() : null,
                    d.TryGetValue("EventIdentifier", out var e) && e is not null ? Convert.ToInt32(e) : null))
                .Take(maxRecords)
                .ToList();
        }
        catch (Exception ex)
        {
            note = "reliability records unavailable: " + ex.Message;
        }

        var ordered = dumps.OrderByDescending(d => d.TimeUtc).ToArray();
        return new ReliabilityReport(ordered, records.ToArray(), note);
    }
}

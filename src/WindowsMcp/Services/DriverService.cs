using System.Collections.Generic;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class DriverService : IDriverService
{
    private readonly IWmiService _wmi;

    public DriverService(IWmiService wmi) => _wmi = wmi;

    public async Task<DriverInfo[]> ListAsync(CancellationToken ct = default)
    {
        var rows = await _wmi.QueryAsync("Win32_PnPSignedDriver", null, null, ct);
        return rows.OfType<IDictionary<string, object>>()
            .Select(d => new DriverInfo(
                DeviceName:    Str(d, "DeviceName"),
                Manufacturer:  Str(d, "Manufacturer"),
                DriverVersion: Str(d, "DriverVersion"),
                DriverDate:    Str(d, "DriverDate"),
                IsSigned:      d.TryGetValue("IsSigned", out var s) && s is not null ? Convert.ToBoolean(s) : null,
                InfName:       Str(d, "InfName")))
            // Many rows are bus/enumerator stubs with no device name; keep only real drivers.
            .Where(x => !string.IsNullOrWhiteSpace(x.DeviceName))
            .ToArray();
    }

    private static string? Str(IDictionary<string, object> d, string key)
        => d.TryGetValue(key, out var v) ? v?.ToString() : null;
}

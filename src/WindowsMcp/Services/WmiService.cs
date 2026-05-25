using System.Management;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Services;

public sealed class WmiService : IWmiService
{
    public Task<object[]> QueryAsync(string className, string? @namespace = null, string? where = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var ns = @namespace ?? "root\\cimv2";
        var wql = string.IsNullOrWhiteSpace(where)
            ? $"SELECT * FROM {className}"
            : $"SELECT * FROM {className} WHERE {where}";

        var scope = new ManagementScope(ns);
        var query = new ObjectQuery(wql);

        using var searcher = new ManagementObjectSearcher(scope, query);
        var rows = searcher.Get()
            .Cast<ManagementObject>()
            .Select(mo => (object)mo.Properties
                .Cast<PropertyData>()
                .ToDictionary(p => p.Name, p => p.Value))
            .ToArray();

        return Task.FromResult(rows);
    }
}

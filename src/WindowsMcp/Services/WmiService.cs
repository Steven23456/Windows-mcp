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

        // ManagementObjectCollection and each ManagementObject are COM-backed and disposable;
        // project to plain dictionaries, then dispose every row + the collection.
        using var collection = searcher.Get();
        var rows = new List<object>();
        foreach (ManagementObject mo in collection)
        {
            using (mo)
            {
                rows.Add(mo.Properties
                    .Cast<PropertyData>()
                    .ToDictionary(p => p.Name, p => p.Value));
            }
        }

        return Task.FromResult(rows.ToArray());
    }
}

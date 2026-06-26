using System.Text.Json;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class FirewallService : IFirewallService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IPowerShellService _ps;

    public FirewallService(IPowerShellService ps) => _ps = ps;

    public async Task<FirewallRuleDto[]> ListAsync(string? nameLike, int max, CancellationToken ct = default)
    {
        // Default to enabled-only + a max cap: Get-NetFirewallRule returns thousands of rules on
        // most boxes; the full dump is multi-MB and overflows token limits.
        var filter = string.IsNullOrWhiteSpace(nameLike)
            ? "Where-Object Enabled -eq 'True'"
            : $"Where-Object {{ $_.Enabled -eq 'True' -and $_.DisplayName -like '*{Esc(nameLike)}*' }}";

        // The interpolated segment carries the filter + cap; the Select-Object calculated
        // properties (literal braces) are a plain concatenated string so no brace-doubling is
        // needed. Enum fields are forced to strings (they serialize as ints otherwise).
        var script =
            $"Get-NetFirewallRule | {filter} | Select-Object -First {max} " +
            "Name,DisplayName," +
            "@{Name='Enabled';Expression={$_.Enabled.ToString()}}," +
            "@{Name='Direction';Expression={$_.Direction.ToString()}}," +
            "@{Name='Action';Expression={$_.Action.ToString()}} " +
            "| ConvertTo-Json -Depth 2";

        var result = await _ps.RunAsync(script, ct);
        return ParseRules(result.Stdout);
    }

    public async Task AddAsync(string name, string direction, string actionType, int port, CancellationToken ct = default)
    {
        var script = $"New-NetFirewallRule -DisplayName '{Esc(name)}' -Direction '{Esc(direction)}' -Action '{Esc(actionType)}' -LocalPort {port} -Protocol TCP";
        var result = await _ps.RunAsync(script, ct);
        if (!result.Success)
            throw new InvalidOperationException($"Firewall add failed: {result.Stderr}");
    }

    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        var script = $"Remove-NetFirewallRule -DisplayName '{Esc(name)}'";
        var result = await _ps.RunAsync(script, ct);
        if (!result.Success)
            throw new InvalidOperationException($"Firewall remove failed: {result.Stderr}");
    }

    // ConvertTo-Json emits a bare object for a single rule and an array for multiple; handle both.
    internal static FirewallRuleDto[] ParseRules(string? stdout)
    {
        var json = stdout?.Trim();
        if (string.IsNullOrEmpty(json)) return Array.Empty<FirewallRuleDto>();
        if (json[0] == '[')
            return JsonSerializer.Deserialize<FirewallRuleDto[]>(json, JsonOpts) ?? Array.Empty<FirewallRuleDto>();
        var single = JsonSerializer.Deserialize<FirewallRuleDto>(json, JsonOpts);
        return single is null ? Array.Empty<FirewallRuleDto>() : new[] { single };
    }

    // Escape ' for single-quoted PowerShell strings (PS doubles the quote).
    private static string Esc(string s) => s.Replace("'", "''");
}

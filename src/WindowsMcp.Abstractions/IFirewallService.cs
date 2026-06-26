using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IFirewallService
{
    /// <summary>Enabled firewall rules, optionally filtered by a DisplayName substring, capped at <paramref name="max"/>.</summary>
    Task<FirewallRuleDto[]> ListAsync(string? nameLike, int max, CancellationToken ct = default);

    /// <summary>Create an inbound/outbound TCP rule. Throws if the underlying cmdlet fails (e.g. no admin).</summary>
    Task AddAsync(string name, string direction, string actionType, int port, CancellationToken ct = default);

    /// <summary>Delete a rule by display name. Throws if the underlying cmdlet fails.</summary>
    Task RemoveAsync(string name, CancellationToken ct = default);
}

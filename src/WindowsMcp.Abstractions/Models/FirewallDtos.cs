namespace WindowsMcp.Abstractions.Models;

/// <summary>A Windows Firewall rule (enabled-state, direction, and action rendered as strings).</summary>
public record FirewallRuleDto(string? Name, string? DisplayName, string? Enabled, string? Direction, string? Action);

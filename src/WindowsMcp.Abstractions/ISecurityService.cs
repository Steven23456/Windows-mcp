using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface ISecurityService
{
    /// <summary>
    /// Snapshot of firewall, Defender, UAC, and BitLocker status. Probes that require admin
    /// return null fields when run unelevated rather than failing the whole audit.
    /// </summary>
    Task<SecurityAuditDto> AuditAsync(CancellationToken ct = default);
}

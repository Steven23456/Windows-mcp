namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// Result of an Authenticode/trust inspection of a file.
/// <para><see cref="Trusted"/> is the catalog-aware WinVerifyTrust verdict (true for both
/// embedded-signed and catalog-signed Windows components).</para>
/// <para><see cref="Signer"/> is the embedded signature's subject when present; it is null
/// for catalog-signed files (which carry no embedded certificate) even when trusted.</para>
/// </summary>
public record AuthenticodeInfo(bool Trusted, string? Signer);

/// <summary>
/// A Winsock catalog (LSP/base service provider) entry: its catalog id, protocol name,
/// and the backing provider DLL path (environment variables expanded), if resolvable.
/// </summary>
public record LspProviderDto(int CatalogEntryId, string ProtocolName, string? ProviderPath);

/// <summary>
/// Windows security posture snapshot. Fields are null when their probe failed — typically
/// because it needs admin (BitLocker, some firewall profiles) and the server runs unelevated;
/// non-null fields are still meaningful. <see cref="Note"/> is set when the whole audit produced
/// no output (e.g. all probes failed).
/// </summary>
public record SecurityAuditDto(
    bool? FirewallEnabled,
    bool? DefenderRunning,
    int? UacLevel,
    string? BitlockerStatus,
    string? Note = null);

/// <summary>
/// Microsoft Defender posture from Get-MpComputerStatus. Null fields / a set <see cref="Note"/>
/// indicate Defender is absent or its cmdlets are unavailable (e.g. a third-party AV is active).
/// </summary>
public record DefenderStatusDto(
    bool? RealTimeProtectionEnabled,
    bool? AntivirusEnabled,
    bool? IsTamperProtected,
    bool? BehaviorMonitorEnabled,
    string? AntivirusSignatureVersion,
    DateTime? AntivirusSignatureLastUpdated,
    DateTime? QuickScanEndTime,
    DateTime? FullScanEndTime,
    string? Note = null);

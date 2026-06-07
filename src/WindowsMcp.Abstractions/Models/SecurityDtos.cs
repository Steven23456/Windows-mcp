namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// Result of an Authenticode/trust inspection of a file.
/// <para><see cref="Trusted"/> is the catalog-aware WinVerifyTrust verdict (true for both
/// embedded-signed and catalog-signed Windows components).</para>
/// <para><see cref="Signer"/> is the embedded signature's subject when present; it is null
/// for catalog-signed files (which carry no embedded certificate) even when trusted.</para>
/// </summary>
public record AuthenticodeInfo(bool Trusted, string? Signer);

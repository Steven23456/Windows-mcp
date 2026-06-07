using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// Inspects a file's code-signing trust. Catalog-aware so Windows system/driver
/// components (which are signed via security catalogs, not embedded certificates)
/// are correctly reported as trusted.
/// </summary>
public interface IAuthenticodeInspector
{
    /// <summary>
    /// Returns the trust verdict and embedded signer subject for a file. A null or
    /// missing path yields <c>(false, null)</c>; failures are swallowed into an
    /// untrusted result rather than thrown.
    /// </summary>
    AuthenticodeInfo Inspect(string? filePath);
}

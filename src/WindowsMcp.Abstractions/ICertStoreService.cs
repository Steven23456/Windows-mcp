using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface ICertStoreService
{
    /// <summary>
    /// Enumerate certificates in a store. <paramref name="location"/>: LocalMachine or CurrentUser;
    /// <paramref name="storeName"/>: Root, CA, My, etc.
    /// </summary>
    Task<CertInfoDto[]> ListAsync(string location = "LocalMachine", string storeName = "Root", CancellationToken ct = default);
}

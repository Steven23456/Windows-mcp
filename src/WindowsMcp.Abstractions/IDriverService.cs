using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IDriverService
{
    /// <summary>Installed PnP device drivers with version, date, signer, and signed-state.</summary>
    Task<DriverInfo[]> ListAsync(CancellationToken ct = default);
}

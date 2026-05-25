using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IRegistryService
{
    Task<RegistryValueDto> GetAsync(string hive, string path, string? valueName, CancellationToken ct = default);
    Task SetAsync(string hive, string path, string valueName, object data, string kind, CancellationToken ct = default);
}

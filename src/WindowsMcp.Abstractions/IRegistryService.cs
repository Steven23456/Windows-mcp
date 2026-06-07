using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

public interface IRegistryService
{
    Task<RegistryValueDto> GetAsync(string hive, string path, string? valueName, CancellationToken ct = default);
    Task SetAsync(string hive, string path, string valueName, object data, string kind, CancellationToken ct = default);

    /// <summary>
    /// Enumerate every value under a key (one <see cref="RegistryValueDto"/> per value,
    /// with its data and kind). Returns an empty array when the key does not exist —
    /// enumeration of an absent key is a normal, non-exceptional outcome.
    /// </summary>
    Task<RegistryValueDto[]> EnumerateValuesAsync(string hive, string path, CancellationToken ct = default);

    /// <summary>
    /// Enumerate the immediate sub-key names under a key. Returns an empty array when
    /// the key does not exist.
    /// </summary>
    Task<string[]> EnumerateSubKeysAsync(string hive, string path, CancellationToken ct = default);
}

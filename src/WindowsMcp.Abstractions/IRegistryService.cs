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

    /// <summary>
    /// One key in one call: its values and its immediate sub-key names. Unlike the enumerators,
    /// an absent key is a <see cref="KeyNotFoundException"/> (the read shape behind
    /// <c>registry_get</c> without a value name). An empty path lists the hive root.
    /// </summary>
    Task<RegistryKeyDto> ListAsync(string hive, string path, CancellationToken ct = default);

    /// <summary>
    /// Delete one value. Returns whether the value existed; deleting what is not there is not
    /// an error.
    /// </summary>
    Task<bool> DeleteValueAsync(string hive, string path, string valueName, CancellationToken ct = default);

    /// <summary>
    /// Delete a key. Without <paramref name="recursive"/> a key that has sub-keys is refused with
    /// an <see cref="InvalidOperationException"/> naming the flag; with it the tree goes and the
    /// descendant count (taken before the delete) is reported. An absent key reports
    /// <c>Existed:false</c>.
    /// </summary>
    Task<RegistryKeyDeleteResult> DeleteKeyAsync(string hive, string path, bool recursive, CancellationToken ct = default);
}

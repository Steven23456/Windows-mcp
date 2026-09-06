using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// B-8 / roadmap C7: every application this machine can launch by name, built in-process from the
/// two Start Menu <c>.lnk</c> folders and from the WinRT package manager — never from
/// <c>Get-StartApps</c>, which costs a PowerShell cold start and takes the serialization gate.
/// The list is cached in-process and refreshed when it is older than five minutes or when a
/// resolve misses (once — then the miss stands).
/// </summary>
public interface IAppCatalogService
{
    /// <summary>Every entry, deduplicated by name and ordered by name.</summary>
    Task<IReadOnlyList<AppEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// The entry <paramref name="name"/> means: exact → prefix → fuzzy (score 70+). No match is a
    /// <see cref="KeyNotFoundException"/> naming the request and the five nearest entries with
    /// their scores.
    /// </summary>
    Task<AppMatch> ResolveAsync(string name, CancellationToken ct = default);
}

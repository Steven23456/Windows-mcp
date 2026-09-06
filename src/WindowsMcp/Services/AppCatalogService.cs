using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// B-8 / roadmap C7: the app catalog, built in-process from the two Start Menu <c>.lnk</c> folders
/// and from the WinRT package manager, cached for <see cref="CacheTtl"/> and refreshed once on a
/// resolve miss. The two sources and the clock are constructor seams so the cache, the
/// refresh-on-miss and the merge are unit-testable with fakes; the real sources get an
/// <c>Integration</c> test of their own (a mocked collaborator is not evidence — CLAUDE.md).
/// </summary>
public sealed class AppCatalogService : IAppCatalogService
{
    /// <summary>Roadmap C7: five minutes. Enumerating a few hundred packages is not free.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>The real sources: both Start Menu folders and the WinRT package manager.</summary>
    public AppCatalogService() : this(null, null, null) { }

    /// <summary>
    /// The seam. <paramref name="shortcuts"/> and <paramref name="packaged"/> are the two sources
    /// (null = the real one); <paramref name="clock"/> is what the TTL is measured against.
    /// </summary>
    private readonly Func<IEnumerable<AppEntry>> _shortcuts;
    private readonly Func<IEnumerable<AppEntry>> _packaged;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AppEntry>? _cache;
    private DateTimeOffset _stamp;
    private bool _refreshedForMiss;

    internal AppCatalogService(
        Func<IEnumerable<AppEntry>>? shortcuts,
        Func<IEnumerable<AppEntry>>? packaged,
        TimeProvider? clock)
    {
        _shortcuts = shortcuts ?? ScanStartMenu;
        _packaged = packaged ?? ScanPackages;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The catalog, read from the sources at most once per <see cref="CacheTtl"/>.</summary>
    public async Task<IReadOnlyList<AppEntry>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is null || _clock.GetUtcNow() - _stamp > CacheTtl) Refresh();
            return _cache!;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads both sources; one that throws is skipped, the other still lists (an unreadable Start Menu folder must not empty the catalog).</summary>
    private void Refresh()
    {
        var shortcuts = Read(_shortcuts);
        var packaged = Read(_packaged);
        _cache = AppCatalog.Merge(shortcuts, packaged);
        _stamp = _clock.GetUtcNow();
        _refreshedForMiss = false;

        static IEnumerable<AppEntry> Read(Func<IEnumerable<AppEntry>> source)
        {
            try { return source().ToArray(); }
            catch { return []; }
        }
    }

    /// <summary>
    /// The catalog's best entry for <paramref name="name"/>. A miss refreshes the cache once (the
    /// app may have just been installed) and then stands until the TTL turns over.
    /// </summary>
    public async Task<AppMatch> ResolveAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An app name is required.", nameof(name));
        var catalog = await ListAsync(ct).ConfigureAwait(false);
        try
        {
            return AppCatalog.Match(catalog, name);
        }
        catch (KeyNotFoundException)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_refreshedForMiss) throw;
                Refresh();
                _refreshedForMiss = true;
                catalog = _cache!;
            }
            finally { _gate.Release(); }
            return AppCatalog.Match(catalog, name);
        }
    }

    /// <summary>Every <c>.lnk</c> under both Start Menu roots; the file name is the app's name.</summary>
    private static IEnumerable<AppEntry> ScanStartMenu()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
        };
        var entries = new List<AppEntry>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length > 0) entries.Add(new AppEntry(name, "shortcut", file, root));
            }
        }
        return entries;
    }

    /// <summary>Every packaged app the user can launch, by display name and AppUserModelId; a package that refuses is skipped.</summary>
    private static IEnumerable<AppEntry> ScanPackages()
    {
        var entries = new List<AppEntry>();
        var manager = new Windows.Management.Deployment.PackageManager();
        foreach (var package in manager.FindPackagesForUser(""))
        {
            IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry> apps;
            try { apps = package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult(); }
            catch { continue; }
            foreach (var app in apps)
            {
                string name, aumid;
                try { name = app.DisplayInfo.DisplayName; aumid = app.AppUserModelId; }
                catch { continue; }
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(aumid))
                    entries.Add(new AppEntry(name, "packaged", aumid, "package:" + package.Id.FamilyName));
            }
        }
        return entries;
    }
}

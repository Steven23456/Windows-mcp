namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test <b>Skipped</b>, with the reason, when
/// neither Start Menu root holds a shortcut named <c>&lt;shortcutName&gt;.lnk</c> (searched
/// recursively) - the same two folders <see cref="WindowsMcp.Services.AppCatalogService"/> scans.
/// xunit 2.9 has no dynamic skip, so the decision is made in the constructor, at discovery time.
/// <para>
/// For a test whose subject is an app a machine may simply not have (Visual Studio Code is not on
/// the GitHub Windows runner image). A test about an app every Windows has must fail, not skip.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StartMenuShortcutFactAttribute : FactAttribute
{
    public StartMenuShortcutFactAttribute(string shortcutName)
    {
        ShortcutName = shortcutName;
        try
        {
            Skip = SkipReason(shortcutName, StartMenuRoots);
        }
        catch (Exception)
        {
            // The scan is the service's own; if it throws, run the test and let it fail on the real
            // problem. Skipping would hide exactly the kind of source bug the test is there to catch.
            Skip = null;
        }
    }

    /// <summary>The shortcut's file name without <c>.lnk</c> - the app's catalog name.</summary>
    public string ShortcutName { get; }

    /// <summary>The machine-wide and the per-user Start Menu <c>Programs</c> folders.</summary>
    internal static IReadOnlyList<string> StartMenuRoots =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
    ];

    /// <summary>Null when some root holds the shortcut; otherwise the skip message, naming it and the roots searched.</summary>
    internal static string? SkipReason(string shortcutName, IEnumerable<string> roots)
    {
        var searched = roots.ToArray();
        return FindShortcut(shortcutName, searched) is null
            ? $"No '{shortcutName}.lnk' under either Start Menu root ({string.Join("; ", searched)}): this machine does not have the app the test is about."
            : null;
    }

    /// <summary>
    /// The target of the first shortcut named <paramref name="shortcutName"/> (ignoring case) that
    /// <see cref="WindowsMcp.Services.AppCatalogService"/>'s own Start Menu scan lists under
    /// <paramref name="roots"/> - the same scan, so the skip check and the catalog agree by construction.
    /// </summary>
    internal static string? FindShortcut(string shortcutName, IEnumerable<string> roots)
        => WindowsMcp.Services.AppCatalogService.ScanStartMenu(roots)
            .FirstOrDefault(e => string.Equals(e.Name, shortcutName, StringComparison.OrdinalIgnoreCase))
            ?.Target;
}

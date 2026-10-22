using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// The Start Menu source of the app catalog, through its seam <c>AppCatalogService.ScanStartMenu(roots)</c>
/// over throwaway roots under the temp folder (the real source passes the two real roots).
/// <para>
/// The bug this pins (review F6): <c>Directory.EnumerateFiles</c> is lazy, so one unreadable
/// subfolder threw <see cref="UnauthorizedAccessException"/> inside the <c>foreach</c>, outside
/// the <c>try</c>; <c>Refresh</c> swallowed it and the WHOLE shortcut source - both roots - became
/// empty. One locked folder under ProgramData hid every Start Menu app on the machine.
/// </para>
/// <para>
/// The ACL tests are <c>Category=Integration</c>: they put a real deny entry on a folder they
/// created, and take it off again in a <c>finally</c> so the folder can be deleted.
/// </para>
/// </summary>
public sealed class AppCatalogServiceStartMenuScanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wmcp-appscan-" + Guid.NewGuid().ToString("N"));
    private readonly string _rootA;
    private readonly string _rootB;

    public AppCatalogServiceStartMenuScanTests()
    {
        _rootA = Directory.CreateDirectory(Path.Combine(_dir, "ProgramData", "Programs")).FullName;
        _rootB = Directory.CreateDirectory(Path.Combine(_dir, "AppData", "Programs")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Touch(string root, params string[] relative)
    {
        var path = Path.Combine([root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static AppEntry Expected(string path, string root)
        => new(Path.GetFileNameWithoutExtension(path), "shortcut", path, root);

    // ---- what is listed -----------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public void ScanStartMenu_lists_every_lnk_under_both_roots_recursively_as_shortcut_entries()
    {
        var top = Touch(_rootA, "Top App.lnk");
        var nested = Touch(_rootA, "Vendor", "Suite", "Nested App.lnk");
        var other = Touch(_rootB, "Visual Studio Code", "Visual Studio Code.lnk");

        var entries = AppCatalogService.ScanStartMenu([_rootA, _rootB]);

        entries.Should().BeEquivalentTo(new[]
        {
            Expected(top, _rootA),
            Expected(nested, _rootA),
            Expected(other, _rootB),
        }, "the entry is (file name without .lnk, \"shortcut\", the full path, the root it was found under)");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScanStartMenu_matches_the_lnk_extension_ignoring_case()
    {
        var upper = Touch(_rootA, "Upper.LNK");
        var mixed = Touch(_rootA, "Sub", "Mixed.Lnk");

        AppCatalogService.ScanStartMenu([_rootA]).Should().BeEquivalentTo(new[]
        {
            Expected(upper, _rootA),
            Expected(mixed, _rootA),
        });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScanStartMenu_lists_only_shortcut_files()
    {
        var real = Touch(_rootA, "Real.lnk");
        Touch(_rootA, "Site.url");
        Touch(_rootA, "Notes.lnk.txt");
        Touch(_rootA, "Archive.lnkx");
        Touch(_rootA, "readme.txt");
        Directory.CreateDirectory(Path.Combine(_rootA, "Folder.lnk"));

        AppCatalogService.ScanStartMenu([_rootA]).Should().BeEquivalentTo(new[] { Expected(real, _rootA) },
            "a .url, a .lnk.txt, a .lnkx and a directory named like a shortcut are not shortcuts");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScanStartMenu_skips_a_missing_root_and_still_scans_the_other()
    {
        var other = Touch(_rootB, "Other.lnk");
        var missing = Path.Combine(_dir, "no-such-root");

        AppCatalogService.ScanStartMenu([missing, _rootB]).Should().BeEquivalentTo(new[] { Expected(other, _rootB) });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScanStartMenu_lists_hidden_shortcuts_and_shortcuts_under_hidden_or_system_folders()
    {
        // N1, a regression against main: a bare EnumerationOptions skips Hidden | System entries by
        // default, and main's SearchOption overload skipped nothing. Windows marks desktop.ini
        // folders (Accessories, Administrative Tools, Windows PowerShell) System, so the default
        // silently drops whole Start Menu folders, and every app a user hid.
        var systemFolder = Path.Combine(_rootA, "Accessories");
        var hiddenFolder = Path.Combine(_rootA, "Tucked Away");
        var inSystem = Touch(systemFolder, "A.lnk");
        var hiddenFile = Touch(_rootA, "B.lnk");
        var plain = Touch(_rootA, "C.lnk");
        var inHidden = Touch(hiddenFolder, "Deeper", "D.lnk");

        File.SetAttributes(systemFolder, FileAttributes.Directory | FileAttributes.System);
        File.SetAttributes(hiddenFolder, FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        try
        {
            File.GetAttributes(systemFolder).Should().HaveFlag(FileAttributes.System, "precondition");
            File.GetAttributes(hiddenFolder).Should().HaveFlag(FileAttributes.Hidden, "precondition");
            File.GetAttributes(hiddenFile).Should().HaveFlag(FileAttributes.Hidden, "precondition");

            AppCatalogService.ScanStartMenu([_rootA]).Should().BeEquivalentTo(new[]
            {
                Expected(inSystem, _rootA),
                Expected(hiddenFile, _rootA),
                Expected(plain, _rootA),
                Expected(inHidden, _rootA),
            }, "a Hidden or System attribute on a shortcut or on any folder above it does not take it out of the Start Menu scan");
        }
        finally
        {
            File.SetAttributes(systemFolder, FileAttributes.Directory);
            File.SetAttributes(hiddenFolder, FileAttributes.Directory);
            File.SetAttributes(hiddenFile, FileAttributes.Normal);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScanStartMenu_reaches_8_folders_below_a_root_and_no_further()
    {
        // N2's bound, pinned exactly: EnumerationOptions.MaxRecursionDepth = 8 lists the files in
        // the root and in folders down to 8 levels below it (root\L1\...\L8\x.lnk), and nothing in
        // L9 (measured: depth 8 lists L1..L8, depth 7 stops at L7). Real Start Menus nest 2-3 deep.
        var expected = new List<AppEntry> { Expected(Touch(_rootA, "Depth0.lnk"), _rootA) };
        var folder = _rootA;
        string nine = "";
        for (int level = 1; level <= 9; level++)
        {
            folder = Path.Combine(folder, "L" + level);
            var lnk = Touch(folder, $"Depth{level}.lnk");
            if (level <= 8) expected.Add(Expected(lnk, _rootA));
            else nine = lnk;
        }

        var entries = AppCatalogService.ScanStartMenu([_rootA]);

        entries.Should().BeEquivalentTo(expected, "a shortcut 8 folders below the root is listed");
        entries.Should().NotContain(e => e.Target == nine, "one 9 folders below is past the bound");
    }

    [JunctionFact]
    [Trait("Category", "Integration")]
    public void ScanStartMenu_does_not_loop_through_a_junction_back_to_the_root()
    {
        // N2: root\App\a.lnk plus root\App\back -> root. The walk follows the junction, so with no
        // bound it goes round root\App\back\App\back\... until paths hit ~32 K characters: 3,623
        // copies of one shortcut in the reviewer's repro. Bounded at 8 folders it is a handful.
        var app = Path.Combine(_rootA, "App");
        var lnk = Touch(app, "a.lnk");
        var back = Path.Combine(app, "back");
        JunctionFactAttribute.TryCreateJunction(back, _rootA, out var error).Should().BeTrue(
            "the junction probe passed at discovery, so making this one must work: " + error);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var entries = AppCatalogService.ScanStartMenu([_rootA]);
            stopwatch.Stop();

            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "the loop is cut off, not walked to the path-length limit");
            entries.Should().Contain(e => e.Target == lnk, "the real shortcut is still listed");
            entries.Should().OnlyContain(e => e.Name == "a", "every entry is that one shortcut, reached through the loop");
            entries.Count.Should().BeInRange(1, 9, "the depth bound caps how often the loop can reach it");
            var longest = _rootA.Length + 9 * @"\App\back".Length + @"\App\a.lnk".Length;
            entries.Should().OnlyContain(e => e.Target.Length <= longest,
                "no listed path goes deeper than the root plus 9 levels of App\\back");
        }
        finally
        {
            // The link first, non-recursively (removes the junction only), so the tree's own
            // cleanup can never recurse through it into the root.
            if (Directory.Exists(back)) Directory.Delete(back);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScanStartMenu_of_empty_roots_is_an_empty_list()
    {
        AppCatalogService.ScanStartMenu([_rootA, _rootB]).Should().BeEmpty();
        AppCatalogService.ScanStartMenu([]).Should().BeEmpty();
    }

    // ---- what an unreadable folder must not take with it ---------------------------------------

    /// <summary>Denies the current user the right to list <paramref name="directory"/>; returns the rule to remove.</summary>
    private static FileSystemAccessRule DenyListing(string directory)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return rule;
    }

    private static void Allow(string directory, FileSystemAccessRule rule)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.RemoveAccessRuleSpecific(rule);
        info.SetAccessControl(security);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ScanStartMenu_skips_an_unreadable_subfolder_and_lists_everything_else()
    {
        // Folders enumerate alphabetically on NTFS: "Aaa" is read before the locked "Mmm" and "Zzz"
        // after it, so both "keep what was already read" and "carry on past it" are pinned, and
        // the second root is scanned after the first one hit the bad folder.
        var top = Touch(_rootA, "Top.lnk");
        var before = Touch(_rootA, "Aaa", "Before.lnk");
        var locked = Path.GetDirectoryName(Touch(_rootA, "Mmm", "Hidden.lnk"))!;
        var after = Touch(_rootA, "Zzz", "Deep", "After.lnk");
        var other = Touch(_rootB, "Other.lnk");

        var rule = DenyListing(locked);
        try
        {
            var probe = () => Directory.EnumerateFiles(locked).ToArray();
            probe.Should().Throw<UnauthorizedAccessException>(
                "precondition: the deny entry has to make the folder unreadable, or this test proves nothing");

            var entries = AppCatalogService.ScanStartMenu([_rootA, _rootB]);

            entries.Should().BeEquivalentTo(new[]
            {
                Expected(top, _rootA),
                Expected(before, _rootA),
                Expected(after, _rootA),
                Expected(other, _rootB),
            }, "only the unreadable folder's shortcut is lost - not its root, and not the other root");
        }
        finally
        {
            Allow(locked, rule);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ScanStartMenu_skips_an_unreadable_root_and_still_scans_the_other()
    {
        Touch(_rootA, "Hidden.lnk");
        var other = Touch(_rootB, "Sub", "Other.lnk");

        var rule = DenyListing(_rootA);
        try
        {
            var probe = () => Directory.EnumerateFiles(_rootA).ToArray();
            probe.Should().Throw<UnauthorizedAccessException>("precondition: the root has to be unreadable");

            AppCatalogService.ScanStartMenu([_rootA, _rootB]).Should().BeEquivalentTo(new[] { Expected(other, _rootB) },
                "an unreadable root costs its own shortcuts, never the other root's");
        }
        finally
        {
            Allow(_rootA, rule);
        }
    }
}

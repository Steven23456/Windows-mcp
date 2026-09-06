using System.Diagnostics;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-8 through the <b>real</b> sources: the two Start Menu <c>.lnk</c> folders and the WinRT
/// package manager. <see cref="AppCatalogServiceTests"/> drives the cache with hand-written
/// entries and would stay green if <c>FindPackagesForUser</c> were never called or the Start Menu
/// scan returned nothing — the exact failure mode CLAUDE.md records for
/// <c>disk_inspect mode:reclaimable</c>. This is the class that fails when a source is dead.
/// <para>
/// <c>Category=Integration</c>: enumerating packages and reading two folders is read-only, opens
/// no window, injects nothing and starts no process. Nothing here launches an app — the two heavy
/// ones the "done when" bar names (Edge, Visual Studio Code) are pinned as <i>catalog</i> facts
/// here and never opened; only Calculator and Notepad are launched, in the desktop bracket.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class AppCatalogServiceIntegrationTests
{
    private static AppCatalogService NewService() => new();

    [Fact]
    public async Task ListAsync_returns_the_apps_this_machine_actually_has()
    {
        // Non-vacuity guard, the same shape as WindowServiceTests' "the session has windows":
        // every invariant below is trivially true of an empty list.
        var list = await NewService().ListAsync();

        list.Should().NotBeEmpty("a Windows session has a Start Menu and packaged apps");
        list.Should().HaveCountGreaterThan(20, "88 shortcuts and ~69 packaged apps were counted on this box");
    }

    [Theory]
    [InlineData("Calculator")]
    [InlineData("Notepad")]
    public async Task ListAsync_contains_the_packaged_apps_every_Windows_11_has(string name)
    {
        var list = await NewService().ListAsync();

        var entry = list.Should().ContainSingle(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Subject;
        entry.Kind.Should().Be("packaged", "these ship as MSIX apps, not as Start Menu shortcuts");
        entry.Target.Should().Contain("!", "a packaged entry's target is an AUMID: <family>!<app id>");
        entry.Source.Should().StartWith("package:");
    }

    [Fact]
    public async Task ListAsync_contains_Microsoft_Edge_as_a_start_menu_shortcut()
    {
        var list = await NewService().ListAsync();

        var edge = list.Should().ContainSingle(e => e.Name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)).Subject;
        edge.Kind.Should().Be("shortcut");
        edge.Target.Should().EndWith(".lnk");
        File.Exists(edge.Target).Should().BeTrue("the target is the shortcut ShellExecute will open");
    }

    [Fact]
    public async Task ListAsync_reads_both_start_menu_roots()
    {
        // ProgramData holds the machine-wide shortcuts, APPDATA the per-user ones; a scan that
        // forgot one of the two folders loses half the Start Menu (Visual Studio Code lives in
        // the per-user one on this box).
        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs");

        var list = await NewService().ListAsync();
        var sources = list.Where(e => e.Kind == "shortcut").Select(e => e.Target).ToArray();

        sources.Should().Contain(t => t.StartsWith(programData, StringComparison.OrdinalIgnoreCase),
            "the machine-wide Start Menu folder is one of the two sources");
        sources.Should().Contain(t => t.StartsWith(appData, StringComparison.OrdinalIgnoreCase),
            "the per-user Start Menu folder is the other");
    }

    [Fact]
    public async Task ListAsync_finds_shortcuts_in_subfolders()
    {
        // "Visual Studio Code\Visual Studio Code.lnk" - the scan has to recurse, and the entry's
        // name is the file name without the extension, not the folder's.
        var list = await NewService().ListAsync();

        list.Where(e => e.Kind == "shortcut")
            .Should().Contain(e => e.Target.Count(c => c == Path.DirectorySeparatorChar)
                                   > e.Source.Count(c => c == Path.DirectorySeparatorChar) + 1,
                "at least one shortcut lives below the root folder it was scanned from");
    }

    [Fact]
    public async Task ListAsync_returns_one_entry_per_name_ordered_by_name()
    {
        var list = await NewService().ListAsync();

        list.Select(e => e.Name).Should().OnlyHaveUniqueItems("the merge deduplicates by name");
        list.Select(e => e.Name).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        list.Should().OnlyContain(e => e.Name.Trim().Length > 0, "a nameless app cannot be launched by name");
        list.Should().OnlyContain(e => e.Kind == "shortcut" || e.Kind == "packaged");
        list.Should().OnlyContain(e => e.Target.Length > 0);
    }

    [Fact]
    public async Task ListAsync_twice_in_a_row_is_served_from_the_cache()
    {
        var service = NewService();
        var first = await service.ListAsync();

        var stopwatch = Stopwatch.StartNew();
        var second = await service.ListAsync();
        stopwatch.Stop();

        second.Select(e => e.Name).Should().Equal(first.Select(e => e.Name));
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(250,
            "enumerating ~150 packages took the best part of a second cold; the cached call cannot be doing it again");
    }

    [Theory]
    [InlineData("calc", "Calculator")]
    [InlineData("notepad", "Notepad")]
    [InlineData("edge", "Microsoft Edge")]
    public async Task ResolveAsync_finds_the_apps_the_done_when_bar_names(string request, string expected)
    {
        var match = await NewService().ResolveAsync(request);

        match.Entry.Name.Should().Be(expected);
        match.Score.Should().BeGreaterThanOrEqualTo(70);
    }

    [Fact]
    public async Task ResolveAsync_finds_Visual_Studio_Code_by_its_full_name_and_by_vs_code()
    {
        // Environmental precondition, called out on its own so a box without VS Code fails only
        // this test: the "done when" bar names launch("visual studio code"), and the catalog is
        // where that resolution has to work. The app is never launched by the suite - it is far
        // too heavy - so this is the whole proof for it.
        var service = NewService();

        (await service.ResolveAsync("visual studio code")).Entry.Name.Should().Be("Visual Studio Code");
        var fuzzy = await service.ResolveAsync("vs code");
        fuzzy.Entry.Name.Should().Be("Visual Studio Code");
        fuzzy.Strategy.Should().Be("fuzzy");
    }

    [Fact]
    public async Task ResolveAsync_refuses_a_name_this_machine_has_nothing_like()
    {
        var act = () => NewService().ResolveAsync("zzqq-not-an-app-zzqq");

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain("zzqq-not-an-app-zzqq");
        message.Length.Should().BeGreaterThan(40, "the five nearest names and their scores are in the message");
    }
}

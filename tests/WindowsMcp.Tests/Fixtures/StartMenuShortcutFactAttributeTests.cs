using FluentAssertions;
using Xunit;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// The skip attribute testing itself. A precondition that never skips makes CI red on a machine
/// without the app; one that always skips hides a real failure on a machine with it - both look
/// like a passing suite. So the lookup is pinned over throwaway Start Menu roots under the temp
/// folder, and the real-roots constructor is checked once against a name no machine has.
/// </summary>
public sealed class StartMenuShortcutFactAttributeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wmcp-startmenu-" + Guid.NewGuid().ToString("N"));
    private readonly string _machineRoot;
    private readonly string _userRoot;

    public StartMenuShortcutFactAttributeTests()
    {
        _machineRoot = Directory.CreateDirectory(Path.Combine(_dir, "ProgramData", "Programs")).FullName;
        _userRoot = Directory.CreateDirectory(Path.Combine(_dir, "AppData", "Programs")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Touch(string root, params string[] relative)
    {
        var path = Path.Combine([root, .. relative]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindShortcut_finds_the_lnk_in_a_subfolder_of_the_second_root()
    {
        // Where VS Code puts it: the per-user root, one folder down, named after the app.
        var lnk = Touch(_userRoot, "Visual Studio Code", "Visual Studio Code.lnk");

        StartMenuShortcutFactAttribute.FindShortcut("Visual Studio Code", [_machineRoot, _userRoot])
            .Should().Be(lnk);
        StartMenuShortcutFactAttribute.SkipReason("Visual Studio Code", [_machineRoot, _userRoot])
            .Should().BeNull("the shortcut exists, so the test must run");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindShortcut_ignores_the_case_of_the_name()
    {
        var lnk = Touch(_machineRoot, "Visual Studio Code.lnk");

        StartMenuShortcutFactAttribute.FindShortcut("visual studio code", [_machineRoot, _userRoot])
            .Should().Be(lnk);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SkipReason_names_the_shortcut_and_both_roots_when_only_lookalikes_exist()
    {
        // The GitHub runner's shape: Visual Studio, but not VS Code. Neither a shorter name, a
        // longer one, nor the right name with another extension is the shortcut.
        Touch(_machineRoot, "Visual Studio.lnk");
        Touch(_machineRoot, "Visual Studio 2022", "Visual Studio Code - Insiders.lnk");
        Touch(_userRoot, "Visual Studio Code.url");
        var missing = Path.Combine(_dir, "no-such-root");

        StartMenuShortcutFactAttribute.FindShortcut("Visual Studio Code", [missing, _machineRoot, _userRoot])
            .Should().BeNull();
        var reason = StartMenuShortcutFactAttribute.SkipReason("Visual Studio Code", [missing, _machineRoot, _userRoot]);

        reason.Should().NotBeNull("a missing shortcut must skip the test, not fail it");
        reason.Should().Contain("'Visual Studio Code.lnk'").And.Contain(_machineRoot).And.Contain(_userRoot,
            "the skip reason is what a reader of the CI log sees: what was looked for, and where");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Constructor_skips_against_the_real_start_menu_when_the_machine_has_no_such_shortcut()
    {
        // Read-only: two directory listings of this machine's real Start Menu roots.
        var name = "wmcp-no-such-app-" + Guid.NewGuid().ToString("N");

        var attribute = new StartMenuShortcutFactAttribute(name);

        attribute.ShortcutName.Should().Be(name);
        attribute.Skip.Should().Contain(name);
        attribute.Skip.Should().Contain(StartMenuShortcutFactAttribute.StartMenuRoots[0])
            .And.Contain(StartMenuShortcutFactAttribute.StartMenuRoots[1],
                "the constructor searches the same two roots AppCatalogService scans");
    }
}

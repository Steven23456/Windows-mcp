using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ShortcutResolverTests
{
    [Fact]
    public void ResolveTarget_resolves_a_real_start_menu_shortcut()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var lnk = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (lnk is null) return; // no shortcuts present: nothing to assert in this environment

        var target = new ShortcutResolver().ResolveTarget(lnk);

        target.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ResolveTarget_returns_input_unchanged_for_non_lnk()
    {
        new ShortcutResolver().ResolveTarget(@"C:\foo\bar.exe").Should().Be(@"C:\foo\bar.exe");
    }

    [Fact]
    public void ResolveTarget_returns_null_for_empty()
    {
        new ShortcutResolver().ResolveTarget("").Should().BeNull();
    }
}

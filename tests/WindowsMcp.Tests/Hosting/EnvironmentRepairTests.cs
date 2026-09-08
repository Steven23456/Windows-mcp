using FluentAssertions;
using WindowsMcp.Hosting;

namespace WindowsMcp.Tests.Hosting;

[Trait("Category", "Unit")]
public class EnvironmentRepairTests
{
    private static Dictionary<string, string> D(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    private static (Dictionary<string, string> set, IReadOnlyList<string> changed) Run(
        Dictionary<string, string> process,
        Dictionary<string, string> machine,
        Dictionary<string, string>? user = null,
        Dictionary<string, string>? defaults = null)
    {
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = EnvironmentRepair.Apply(process, machine, user ?? D(), defaults ?? D(), (k, v) => set[k] = v);
        return (set, changed);
    }

    /// <summary>
    /// The repaired <c>Path</c>, with a failure message that names the rule rather than the
    /// dictionary lookup that missed.
    /// </summary>
    private static string RepairedPath(Dictionary<string, string> set)
    {
        set.Should().ContainKey("Path",
            "C-6 repairs a Path that is empty, missing, or cannot resolve a system command");
        return set["Path"];
    }

    [Fact]
    public void ClaudeDesktopShape_PathExtIsRepairedFromRegistry()
    {
        // The exact defect observed: host passes PATHEXT=.CPL, registry has the real list.
        var process = D(("PATH", @"C:\Windows\system32"), ("PATHEXT", ".CPL"), ("TEMP", @"C:\t"));
        var machine = D(("PATHEXT", ".COM;.EXE;.BAT;.CMD;.PY"), ("Path", @"C:\Windows"));

        var (set, changed) = Run(process, machine);

        set["PATHEXT"].Should().Be(".COM;.EXE;.BAT;.CMD;.PY");
        changed.Should().Contain("PATHEXT");
    }

    [Fact]
    public void PathExtWithoutExe_AndNoRegistryValue_FallsBackToDefault()
    {
        var (set, _) = Run(D(("PATHEXT", ".CPL")), D());
        set["PATHEXT"].Should().Be(EnvironmentRepair.DefaultPathExt);
    }

    [Fact]
    public void PathExtMissingEntirely_IsSet()
    {
        var (set, _) = Run(D(), D());
        set.Should().ContainKey("PATHEXT");
        EnvironmentRepair.HasExe(set["PATHEXT"]).Should().BeTrue();
    }

    [Fact]
    public void HealthyPathExt_IsLeftAlone()
    {
        var (set, changed) = Run(D(("PATHEXT", ".exe;.cmd")), D(("PATHEXT", ".COM;.EXE")));
        set.Should().NotContainKey("PATHEXT");
        changed.Should().NotContain("PATHEXT");
    }

    /// <summary>
    /// C-6 changed the policy sentence for exactly one more variable. Everything except
    /// <c>PATHEXT</c> and <c>Path</c> is still untouchable; <c>Path</c> is now appended to (never
    /// reordered, never replaced) when it cannot resolve a system command.
    /// </summary>
    [Fact]
    public void HostProvidedValues_AreNeverOverwritten_ExceptPathExtAndPath()
    {
        var process = D(("Path", @"C:\host-chosen"), ("ProgramData", @"D:\pd"), ("SystemRoot", @"C:\Windows"));
        var machine = D(("Path", @"C:\Windows\System32"), ("ProgramData", @"C:\ProgramData"));

        var (set, _) = Run(process, machine);

        set.Should().NotContainKey("ProgramData", "the host's ProgramData is still its own business");
        RepairedPath(set).Should().Be(@"C:\host-chosen;C:\Windows\System32",
            "C-6: a Path with no System32 cannot resolve where.exe, so the registry is APPENDED - "
            + "the host's own entry still comes first and is still spelled its way");
    }

    // ---- C-6 (R9): the Path rule -------------------------------------------------------------

    [Fact]
    public void PathWithoutSystem32_GetsMachineThenUserAppendedAfterTheHostsEntries()
    {
        var process = D(("Path", @"C:\nothing"), ("PATHEXT", ".EXE"), ("SystemRoot", @"C:\Windows"));
        var machine = D(("Path", @"C:\Windows\System32;C:\Program Files\Git\cmd"));
        var user = D(("Path", @"C:\Users\x\bin"));

        var (set, changed) = Run(process, machine, user);

        RepairedPath(set).Should().Be(@"C:\nothing;C:\Windows\System32;C:\Program Files\Git\cmd;C:\Users\x\bin");
        changed.Where(c => c.Equals("Path", StringComparison.OrdinalIgnoreCase))
            .Should().HaveCount(1, "the startup line names each repaired variable once");
    }

    [Fact]
    public void PathWithSystem32_IsLeftAlone_EvenWhenItIsTheOnlyEntry()
    {
        var process = D(("Path", @"C:\Windows\System32"), ("PATHEXT", ".EXE"), ("SystemRoot", @"C:\Windows"));
        var machine = D(("Path", @"C:\Windows;C:\Program Files\Git\cmd"));

        var (set, changed) = Run(process, machine);

        set.Should().NotContainKey("Path", "a short Path may be deliberate; a Path without System32 cannot be");
        changed.Should().NotContain("Path");
    }

    [Fact]
    public void EmptyPath_TakesTheRegistryWithoutALeadingSeparator()
    {
        var process = D(("Path", ""), ("PATHEXT", ".EXE"), ("SystemRoot", @"C:\Windows"));
        var machine = D(("Path", @"C:\Windows\System32"));

        var (set, changed) = Run(process, machine);

        RepairedPath(set).Should().Be(@"C:\Windows\System32");
        changed.Where(c => c.Equals("Path", StringComparison.OrdinalIgnoreCase)).Should().HaveCount(1);
    }

    [Fact]
    public void MissingPath_IsFilledFromTheRegistryAndNamedOnce()
    {
        var process = D(("PATHEXT", ".EXE"), ("SystemRoot", @"C:\Windows"));
        var machine = D(("Path", @"C:\Windows\System32"));
        var user = D(("Path", @"C:\Users\x\bin"));

        var (set, changed) = Run(process, machine, user);

        RepairedPath(set).Should().Be(@"C:\Windows\System32;C:\Users\x\bin");
        changed.Where(c => c.Equals("Path", StringComparison.OrdinalIgnoreCase))
            .Should().HaveCount(1, "filled and then checked is still one repair, reported once");
    }

    /// <summary>
    /// C-6: a <c>Path</c> that is <b>both</b> filled from the registry and then repaired is still
    /// named once on the startup line. The fill's own join (machine + ";" + user, verbatim) and
    /// <see cref="PathMerge.Merge"/> disagree here — the registry lists the same directory twice —
    /// so the repair really does rewrite what the fill just set, which is the only way the second
    /// <c>changed.Add("Path")</c> can be reached.
    /// </summary>
    [Fact]
    public void PathFilledFromTheRegistryAndThenRepaired_IsStillNamedOnce()
    {
        var process = D(("PATHEXT", ".EXE"), ("SystemRoot", @"C:\Windows"));
        var machine = D(("Path", @"C:\tools;"));
        var user = D(("Path", @"C:\Tools"));

        var (set, changed) = Run(process, machine, user);

        RepairedPath(set).Should().Be(@"C:\tools",
            "the merge drops the duplicate the raw fill left behind, keeping the first spelling");
        changed.Where(c => c.Equals("Path", StringComparison.OrdinalIgnoreCase))
            .Should().HaveCount(1, "filled and then repaired is one repaired variable, reported once");
    }

    [Fact]
    public void PathWithoutSystem32_AndAnEmptyRegistry_FallsBackToTheStockFour()
    {
        var process = D(("Path", @"C:\nothing"), ("PATHEXT", ".EXE"));
        var defaults = D(("SystemRoot", @"C:\Windows"));

        var (set, changed) = Run(process, D(), defaults: defaults);

        RepairedPath(set).Should().Be(
            @"C:\nothing;C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem;C:\Windows\System32\WindowsPowerShell\v1.0",
            "a box whose registry read gave nothing must still resolve where.exe and powershell.exe");
        changed.Where(c => c.Equals("Path", StringComparison.OrdinalIgnoreCase)).Should().HaveCount(1);
    }

    [Fact]
    public void PathWithoutSystem32_AndNoSystemRootAnywhere_IsLeftAlone()
    {
        var process = D(("Path", @"C:\nothing"), ("PATHEXT", ".EXE"));

        var (set, changed) = Run(process, D());

        set.Should().NotContainKey("Path", "there is nothing to append: no registry Path and no SystemRoot to guess one from");
        changed.Should().NotContain("Path");
    }

    // ---- C-6 (R9): StockPath, the fallback when the registry gave nothing --------------------

    [Fact]
    public void StockPath_is_the_four_directories_a_stock_install_puts_first_in_order()
        => EnvironmentRepair.StockPath(@"C:\Windows").Should().Be(
            @"C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem;C:\Windows\System32\WindowsPowerShell\v1.0",
            "System32 first (where.exe, cmd.exe), then the root, Wbem, and Windows PowerShell - "
            + "the order Windows itself writes them in");

    [Fact]
    public void StockPath_names_System32_first_so_a_repaired_Path_can_resolve_a_command()
    {
        var entries = PathMerge.Split(EnvironmentRepair.StockPath(@"C:\Windows"));

        entries.Should().HaveCount(4);
        entries[0].Should().Be(@"C:\Windows\System32");
        PathMerge.HasSystem32(EnvironmentRepair.StockPath(@"C:\Windows"), @"C:\Windows").Should().BeTrue(
            "the fallback exists precisely to satisfy the check that triggered it");
    }

    [Theory]
    [InlineData(@"C:\Windows\")]
    [InlineData("C:\\Windows/")]
    [InlineData(@"  C:\Windows  ")]
    [InlineData(@"  C:\Windows\  ")]
    public void StockPath_does_not_double_the_separator_of_a_root_that_carries_one(string systemRoot)
        => EnvironmentRepair.StockPath(systemRoot).Should().Be(
            @"C:\Windows\System32;C:\Windows;C:\Windows\System32\Wbem;C:\Windows\System32\WindowsPowerShell\v1.0",
            @"C:\Windows\\System32 is not a directory any child can search");

    [Fact]
    public void StockPath_of_a_non_default_root_uses_that_root_throughout()
        => EnvironmentRepair.StockPath(@"D:\Win").Should().Be(
            @"D:\Win\System32;D:\Win;D:\Win\System32\Wbem;D:\Win\System32\WindowsPowerShell\v1.0",
            "SystemRoot is not always C:\\Windows, and a hard-coded C: would repair the wrong box");

    [Fact]
    public void SystemRoot_ComesFromTheProcessBlockBeforeTheDefaults()
    {
        // The host says SystemRoot is D:\Win, so C:\Windows\System32 is somebody else's System32
        // and this Path still cannot resolve a system command.
        var process = D(("Path", @"C:\Windows\System32"), ("PATHEXT", ".EXE"), ("SystemRoot", @"D:\Win"));
        var machine = D(("Path", @"D:\Win\System32"));
        var defaults = D(("SystemRoot", @"C:\Windows"));

        var (set, _) = Run(process, machine, defaults: defaults);

        RepairedPath(set).Should().Be(@"C:\Windows\System32;D:\Win\System32");
    }

    [Fact]
    public void SystemRoot_ComesFromTheRegistryBeforeTheDefaults()
    {
        var process = D(("Path", @"C:\Windows\System32"), ("PATHEXT", ".EXE"));
        var machine = D(("SystemRoot", @"D:\Win"), ("Path", @"D:\Win\System32"));
        var defaults = D(("SystemRoot", @"C:\Windows"));

        var (set, _) = Run(process, machine, defaults: defaults);

        RepairedPath(set).Should().Be(@"C:\Windows\System32;D:\Win\System32",
            "the registry's SystemRoot outranks the folder default when the host set none");
    }

    [Fact]
    public void MissingRegistryVariables_AreFilled_UserOverridesMachine()
    {
        var machine = D(("ComSpec", @"C:\Windows\system32\cmd.exe"), ("Foo", "machine"));
        var user = D(("Foo", "user"), ("OneDrive", @"C:\Users\x\OneDrive"));

        var (set, changed) = Run(D(("PATHEXT", ".EXE")), machine, user);

        set["ComSpec"].Should().Be(@"C:\Windows\system32\cmd.exe");
        set["Foo"].Should().Be("user");
        set["OneDrive"].Should().Be(@"C:\Users\x\OneDrive");
        changed.Should().BeEquivalentTo(["ComSpec", "Foo", "OneDrive"]);
    }

    [Fact]
    public void MissingPath_IsMachineThenUser_Joined()
    {
        var machine = D(("Path", @"C:\Windows;C:\Windows\system32;"));
        var user = D(("Path", @"C:\Users\x\bin"));

        var (set, _) = Run(D(("PATHEXT", ".EXE")), machine, user);

        RepairedPath(set).Should().Be(@"C:\Windows;C:\Windows\system32;C:\Users\x\bin");
    }

    [Fact]
    public void Defaults_OnlyFillWhatRegistryAndHostLeftEmpty()
    {
        var machine = D(("ProgramData", @"C:\ProgramData"));
        var defaults = D(("ProgramData", @"X:\wrong"), ("OS", "Windows_NT"), ("Empty", ""));

        var (set, _) = Run(D(("PATHEXT", ".EXE")), machine, defaults: defaults);

        set["ProgramData"].Should().Be(@"C:\ProgramData");
        set["OS"].Should().Be("Windows_NT");
        set.Should().NotContainKey("Empty");
    }

    [Fact]
    public void KeyMatching_IsCaseInsensitive()
    {
        // The host spells it "path"; the registry spells it "Path". One variable, not two - so
        // nothing is filled (and C-6 leaves it alone: this one already carries System32).
        var process = D(("path", @"C:\Windows\System32"), ("PATHEXT", ".EXE"), ("SystemRoot", @"C:\Windows"));

        var (set, _) = Run(process, D(("Path", @"C:\y")));

        set.Should().NotContainKey("Path");
    }

    [Theory]
    [InlineData(".CPL", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(".COM;.EXE;.BAT", true)]
    [InlineData(".exe", true)]
    [InlineData(" .EXE ; .CMD", true)]
    [InlineData(".EXEC", false)]
    public void HasExe_DetectsExeEntry(string? value, bool expected) =>
        EnvironmentRepair.HasExe(value).Should().Be(expected);
}

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

    [Fact]
    public void HostProvidedValues_AreNeverOverwritten()
    {
        var process = D(("Path", @"C:\host-chosen"), ("ProgramData", @"D:\pd"));
        var machine = D(("Path", @"C:\Windows"), ("ProgramData", @"C:\ProgramData"));

        var (set, _) = Run(process, machine);

        set.Should().NotContainKey("Path");
        set.Should().NotContainKey("ProgramData");
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

        set["Path"].Should().Be(@"C:\Windows;C:\Windows\system32;C:\Users\x\bin");
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
        var (set, _) = Run(D(("path", @"C:\x"), ("PATHEXT", ".EXE")), D(("Path", @"C:\y")));
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

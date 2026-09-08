using System.Collections;
using FluentAssertions;
using WindowsMcp.Hosting;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// C-6 (roadmap R9): the <c>Path</c> repair against the <b>real</b> registry, through the same
/// <c>Environment.GetEnvironmentVariables(Machine|User)</c> route <c>EnvironmentRepair.Apply()</c>
/// uses. The unit tests above prove the merge rules on hand-written dictionaries; this one proves
/// the thing the rule exists for — that what the registry actually holds on this box, appended to
/// a host-stripped <c>Path</c>, produces a <c>Path</c> a child process can resolve a command on.
/// <para>
/// Deliberately does NOT call the parameterless <c>Apply()</c>: that mutates the test process's
/// own environment for every test that runs after it.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class EnvironmentRepairPathIntegrationTests
{
    private static Dictionary<string, string> Read(EnvironmentVariableTarget target)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables(target))
            if (e.Key is string k && e.Value is string v) d[k] = v;
        return d;
    }

    [Fact]
    public void A_host_stripped_Path_is_repaired_into_one_that_resolves_where_exe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var process = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\nothing",      // the Claude Desktop shape: a Path that resolves nothing
            ["PATHEXT"] = ".COM;.EXE;.BAT",
            ["SystemRoot"] = systemRoot,
        };
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var changed = EnvironmentRepair.Apply(
            process,
            Read(EnvironmentVariableTarget.Machine),
            Read(EnvironmentVariableTarget.User),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            (k, v) => set[k] = v);

        changed.Should().Contain(c => c.Equals("Path", StringComparison.OrdinalIgnoreCase));
        set["Path"].Should().StartWith(@"C:\nothing", "the host's own entry is never dropped or reordered");
        PathMerge.HasSystem32(set["Path"], systemRoot).Should().BeTrue(
            "the whole point of the rule is that the repaired Path carries the system directory");

        PathMerge.Split(set["Path"])
            .Any(entry => File.Exists(Path.Combine(entry, "where.exe")))
            .Should().BeTrue("a child spawned with this Path must be able to resolve where.exe");
    }
}

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Server;
using WindowsMcp.Hosting;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// S-11 (R5): which tools show the indicator. The set is the design note's list, pinned literally
/// so a change to it is a visible diff; and every name in it must be a tool the assembly really
/// exposes, under its wire name - a name that drifted from a rename would silently stop the
/// indicator for that tool. <see cref="ControlIndicatorFilterTests"/> proves the filter consults
/// the set over the real MCP pipeline.
/// </summary>
[Trait("Category", "Unit")]
public class DrivingToolsTests
{
    /// <summary>
    /// The design note's "Driving tools" list: the tools that inject input or move focus - S-11's
    /// thirteen, plus E-6's <c>sequence</c>, so the filter's BeginAction covers the whole batch and
    /// the takeover hotkey is live between its steps (docs/design/E-6-sequence.md "Mechanism").
    /// </summary>
    internal static readonly string[] DesignNoteNames =
    [
        "click", "drag", "hover", "type", "key", "shortcut", "scroll", "multi_select", "multi_edit",
        "interact_element", "file_dialog", "focus", "switch_to_window", "sequence",
    ];

    /// <summary>
    /// The wire name of every tool <c>WithToolsFromAssembly</c> discovers: the attribute's Name when
    /// set, else the method name in snake_case (file_read = FileRead; see ToolInventoryTests).
    /// </summary>
    internal static string[] WireNames() =>
        typeof(InputTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attr is not null)
            .Select(x => x.Attr!.Name ?? JsonNamingPolicy.SnakeCaseLower.ConvertName(x.Method.Name))
            .ToArray();

    [Fact]
    public void Names_is_exactly_the_fourteen_tools_of_the_design_notes()
    {
        DrivingTools.Names.Should().BeEquivalentTo(DesignNoteNames,
            "the set is the reviewed list in docs/design/S-11-control-indicator.md plus E-6's sequence; a tool "
            + "drifting in or out changes when a person at the machine is told the AI has the controls");
    }

    [Fact]
    public void Sequence_is_a_driving_tool()
    {
        // E-6: without it the filter never begins an action for the batch, the hotkey is not
        // registered between steps, and a takeover mid-sequence cannot happen at all.
        DrivingTools.Names.Should().Contain("sequence");
    }

    [Fact]
    public void Every_driving_tool_name_is_a_tool_the_assembly_exposes()
    {
        var wire = WireNames();

        DrivingTools.Names.Should().HaveCount(14, "an empty set would make the sweep below vacuous");
        DrivingTools.Names.Where(n => !wire.Contains(n, StringComparer.Ordinal))
            .Should().BeEmpty("every driving name is matched against the wire name the client calls, case and underscores included");
    }

    [Fact]
    public void The_wire_name_sweep_reads_real_tool_names()
    {
        // Guards the helper: if the naming rule above were wrong, the sweep would compare against
        // names no client ever sends.
        WireNames().Should().Contain(["click", "multi_select", "switch_to_window", "file_read", "wait", "sequence"]);
    }

    [Theory]
    [InlineData("window")]           // the design note names these three explicitly as never showing it
    [InlineData("launch")]
    [InlineData("powershell")]
    [InlineData("wait")]             // InputTools, but injects nothing
    [InlineData("clipboard")]        // InputTools, but injects nothing
    [InlineData("screenshot")]
    [InlineData("snapshot")]
    [InlineData("wait_for")]
    public void Tools_that_inject_nothing_are_not_driving_tools(string name)
    {
        DrivingTools.Names.Should().NotContain(name);
    }
}

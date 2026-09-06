using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using ModelContextProtocol.Server;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// The tool surface as a whole: how many tools the assembly advertises, and whether the documents
/// that quote that number still agree with it. Nothing pinned the count before B-5, so "65 tools"
/// was repeated in four documents with nothing to notice when a tool was added.
/// </summary>
[Trait("Category", "Unit")]
public class ToolInventoryTests
{
    /// <summary>
    /// Exactly what <c>WithToolsFromAssembly</c> discovers: public methods carrying
    /// <c>[McpServerTool]</c> on a type carrying <c>[McpServerToolType]</c>.
    /// </summary>
    private static MethodInfo[] ToolMethods() =>
        typeof(InputTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

    private static string RepoRoot()
    {
        // Same walk ServerInfoTests uses - no fragile ../../.. count.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Windows-mcp.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must run from inside the repo to read the skill and the docs");
        return dir!.FullName;
    }

    private static string Skill() => File.ReadAllText(Path.Combine(RepoRoot(), "skills", "windows", "SKILL.md"));

    [Fact]
    public void The_assembly_advertises_sixty_six_tools()
    {
        // B-5 takes the count from 65 to 66 (roadmap C3: wait, then multi_select and multi_edit
        // in phase 4). Every number quoted in a document is checked against this one below.
        ToolMethods().Should().HaveCount(66);
    }

    [Fact]
    public void Wait_is_one_of_them()
    {
        ToolMethods().Select(m => m.Name).Should().Contain(nameof(InputTools.Wait));
    }

    [Fact]
    public void Every_tool_carries_a_description()
    {
        // The description is the only spec the model reads; a tool without one is invisible.
        var undescribed = ToolMethods()
            .Where(m => string.IsNullOrWhiteSpace(
                m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description))
            .Select(m => m.Name);

        undescribed.Should().BeEmpty();
    }

    [Fact]
    public void No_tool_description_still_says_it_is_not_implemented()
    {
        // Catches a test-agent stub left in the tree: the placeholder descriptions are the shape
        // "B-5: not implemented yet".
        var stubs = ToolMethods()
            .Where(m => m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!
                .Description.Contains("not implemented", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name);

        stubs.Should().BeEmpty("a stub description would ship to every client as the tool's spec");
    }

    // ---- the documents that quote the count --------------------------------------------------

    [Fact]
    public void The_skill_playbook_offers_wait_instead_of_a_powershell_sleep()
    {
        var skill = Skill();

        skill.Should().Contain("`wait`",
            "the playbook lists the tools by name and steers the model to them; a wait tool the "
            + "skill never mentions is a wait tool the model will not use - run docs-agent");
        skill.Should().NotContain("Start-Sleep",
            "sleeping through the powershell tool pays a cold start and takes the serialization gate");
    }

    [Theory]
    [InlineData("skills/windows/SKILL.md")]
    [InlineData("README.md")]
    // docs/architecture/OVERVIEW.md and ARCHITECTURE.md quote the count too, but they also quote
    // per-group counts ("5 tools", "9 tools") that no regex separates from the total, so they are
    // docs-agent's to keep in step by hand.
    public void Every_document_that_quotes_a_tool_count_quotes_the_real_one(string relative)
    {
        // Not a doc style rule: this number is what a reader - and the model reading the skill -
        // treats as the tool inventory. docs-agent owns the edit; this is what tells it to.
        var path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"expected {relative} in the repo");
        int expected = ToolMethods().Length;

        var quoted = Regex.Matches(File.ReadAllText(path), @"(\d+)\s+(?:MCP\s+)?(?:atomic\s+)?tools")
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .ToArray();

        quoted.Should().NotBeEmpty($"{relative} advertises the tool inventory");
        quoted.Should().OnlyContain(n => n == expected,
            $"{relative} says {string.Join("/", quoted)} tools and the assembly has {expected} - run docs-agent");
    }
}

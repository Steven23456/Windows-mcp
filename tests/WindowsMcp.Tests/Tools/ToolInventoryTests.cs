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
    public void The_assembly_advertises_sixty_nine_tools()
    {
        // B-5 took the count from 65 to 66; B-7's multi_select and multi_edit took it to 68;
        // C-2's registry_delete is the sixty-ninth (roadmap R6: "68 -> 69"). Every number quoted
        // in a document is checked against this one below.
        ToolMethods().Should().HaveCount(69);
    }

    /// <summary>C-2 (R5): the new tool is on the surface, by name and not just by count.</summary>
    [Fact]
    public void Registry_delete_is_the_sixty_ninth()
    {
        ToolMethods().Select(m => m.Name).Should().Contain(nameof(RegistryTools.RegistryDelete));
    }

    [Fact]
    public void Wait_is_one_of_them()
    {
        ToolMethods().Select(m => m.Name).Should().Contain(nameof(InputTools.Wait));
    }

    [Fact]
    public void The_two_batch_tools_are_the_sixty_seventh_and_sixty_eighth()
    {
        // Named, not just counted: C3's whole argument for spending two tool slots is that the
        // model reaches for upstream's names by habit.
        ToolMethods().Select(m => m.Name).Should()
            .Contain(nameof(InputTools.MultiSelect)).And.Contain(nameof(InputTools.MultiEdit));
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

    // ---- C-7 (R10): the annotation table -----------------------------------------------------

    /// <summary>
    /// The arguments actually written in source. <c>McpServerToolAttribute.ReadOnly</c> and friends
    /// read the same by reflection whether a tool set them to the SDK default or never set them at
    /// all, so "explicit" can only be asserted through <see cref="CustomAttributeData"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> NamedArguments(MethodInfo m) =>
        m.GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(McpServerToolAttribute))
            .NamedArguments.ToDictionary(a => a.MemberName, a => a.TypedValue.Value);

    /// <summary>Tool method names whose attribute writes <paramref name="argument"/> = true.</summary>
    private static string[] ToolsWith(string argument) =>
        ToolMethods()
            .Where(m => NamedArguments(m).TryGetValue(argument, out var value) && value is true)
            .Select(m => m.Name)
            .ToArray();

    /// <summary>
    /// The C-7 note's ReadOnly column, as tool method names (the wire name is the snake_case of
    /// the method: file_read = FileRead). A literal list, so any later change to the table is a
    /// visible diff rather than drift.
    /// </summary>
    private static readonly string[] ReadOnlyTools =
    [
        nameof(UIAutomationTools.Snapshot), nameof(UIAutomationTools.GetState),
        nameof(UIAutomationTools.GetElement), nameof(UIAutomationTools.GetText),
        nameof(UIAutomationTools.GetTable), nameof(UIAutomationTools.FindElement),
        nameof(UIAutomationTools.AssertElement), nameof(UIAutomationTools.WaitFor),
        nameof(InputTools.Wait),
        nameof(WindowTools.MultiMonitor),
        nameof(ScreenTools.Screenshot), nameof(ScreenTools.Ocr),
        nameof(ProcessTools.ProcessInspect), nameof(ProcessTools.EventLog),
        nameof(SystemTools.SystemInfo), nameof(SystemTools.WmiQuery),
        nameof(SystemTools.Reliability), nameof(SystemTools.DriverList),
        nameof(FileTools.FileRead), nameof(FileTools.FileSearch), nameof(FileTools.FileInfo),
        nameof(FileTools.FileHash), nameof(FileTools.FileStreams),
        nameof(DiskTools.DiskInspect), nameof(StorageTools.StorageHealth),
        nameof(RegistryTools.RegistryGet),
        nameof(NetworkTools.Network), nameof(WebTools.Scrape),
        nameof(SecurityTools.DefenderStatus), nameof(SystemTools.SecurityAudit),
        nameof(SecurityTools.VerifySignature), nameof(SecurityTools.CertStore),
        nameof(StartupTools.StartupReport), nameof(UsnTools.FsChanges),
    ];

    /// <summary>The note's OpenWorld column: reaches past this machine, or runs arbitrary code.</summary>
    private static readonly string[] OpenWorldTools =
    [
        nameof(WindowTools.Launch), nameof(ProcessTools.StartProcess), nameof(ShellTools.Powershell),
        nameof(NetworkTools.Network), nameof(WebTools.HttpRequest), nameof(WebTools.Scrape),
    ];

    /// <summary>The note's Destructive column: ends or replaces something durable.</summary>
    private static readonly string[] DestructiveTools =
    [
        nameof(WindowTools.Window), nameof(ProcessTools.Process), nameof(ShellTools.Powershell),
        nameof(JobTools.Job), nameof(SystemTools.Env), nameof(SystemTools.PowerAction),
        nameof(FileTools.FileWrite), nameof(FileTools.FileManage), nameof(FileTools.Archive),
        nameof(ProcessTools.Service), nameof(ProcessTools.ScheduledTask),
        nameof(RegistryTools.RegistrySet), nameof(RegistryTools.RegistryDelete),
        nameof(NetworkTools.Firewall), nameof(WebTools.HttpRequest),
    ];

    /// <summary>The note's Idempotent column: repeating the call leaves the same state.</summary>
    private static readonly string[] IdempotentTools =
    [
        nameof(UIAutomationTools.Snapshot), nameof(UIAutomationTools.GetState),
        nameof(UIAutomationTools.GetElement), nameof(UIAutomationTools.GetText),
        nameof(UIAutomationTools.GetTable), nameof(UIAutomationTools.FindElement),
        nameof(UIAutomationTools.AssertElement), nameof(UIAutomationTools.WaitFor),
        nameof(InputTools.Wait), nameof(InputTools.Hover), nameof(InputTools.Clipboard),
        nameof(WindowTools.Focus), nameof(WindowTools.SwitchToWindow), nameof(WindowTools.MultiMonitor),
        nameof(ScreenTools.Screenshot), nameof(ScreenTools.Ocr),
        nameof(ProcessTools.ProcessInspect), nameof(ProcessTools.EventLog),
        nameof(JobTools.Job),
        nameof(SystemTools.SystemInfo), nameof(SystemTools.WmiQuery), nameof(SystemTools.Env),
        nameof(SystemTools.Reliability), nameof(SystemTools.DriverList), nameof(SystemTools.Audio),
        // C-1: file_write left this set - `append: true` twice is not the same state twice.
        nameof(FileTools.FileRead), nameof(FileTools.FileSearch),
        nameof(FileTools.FileInfo), nameof(FileTools.FileHash), nameof(FileTools.FileStreams),
        nameof(FileTools.Archive),
        nameof(DiskTools.DiskInspect), nameof(StorageTools.StorageHealth),
        nameof(RegistryTools.RegistryGet), nameof(RegistryTools.RegistrySet),
        nameof(RegistryTools.RegistryDelete),
        nameof(NetworkTools.Network), nameof(WebTools.Scrape),
        nameof(SecurityTools.DefenderStatus), nameof(SystemTools.SecurityAudit),
        nameof(SecurityTools.VerifySignature), nameof(SecurityTools.CertStore),
        nameof(IntegrityTools.Integrity), nameof(StartupTools.StartupReport), nameof(UsnTools.FsChanges),
    ];

    /// <summary>C-7 R1: nothing is left to the SDK's defaults.</summary>
    [Fact]
    public void Every_tool_names_all_five_annotation_arguments()
    {
        string[] required = ["Title", "ReadOnly", "Destructive", "Idempotent", "OpenWorld"];

        var incomplete = ToolMethods()
            .Select(m => new { m.Name, Missing = required.Except(NamedArguments(m).Keys).ToArray() })
            .Where(x => x.Missing.Length > 0)
            .Select(x => $"{x.Name} is missing {string.Join("+", x.Missing)}")
            .ToArray();

        incomplete.Should().BeEmpty(
            "a hint the attribute never writes is indistinguishable from the SDK default, and the "
            + "SDK's defaults advertise every tool as destructive, not read-only and open-world");
    }

    /// <summary>C-7 R1: the title is what a client shows instead of the snake_case name.</summary>
    [Fact]
    public void Every_title_is_a_short_non_blank_phrase()
    {
        var titles = ToolMethods()
            .Select(m => new { m.Name, Title = NamedArguments(m).GetValueOrDefault("Title") as string })
            .ToArray();

        titles.Where(t => string.IsNullOrWhiteSpace(t.Title)).Select(t => t.Name)
            .Should().BeEmpty("a blank title is worse than none - the client shows an empty label");
        titles.Where(t => t.Title is { Length: > 40 }).Select(t => $"{t.Name}: {t.Title}")
            .Should().BeEmpty("titles are short phrases; 40 characters is already generous");
    }

    /// <summary>C-7 R2: the ReadOnly column of the note's table, pinned literally.</summary>
    [Fact]
    public void The_read_only_set_is_the_tables_read_only_set()
    {
        ToolsWith("ReadOnly").Should().BeEquivalentTo(ReadOnlyTools,
            "the C-7 table is the reviewed classification; a tool that drifts in or out of it "
            + "changes what a client auto-approves");
    }

    /// <summary>C-7 R2: the OpenWorld column.</summary>
    [Fact]
    public void The_open_world_set_is_the_tables_open_world_set()
    {
        ToolsWith("OpenWorld").Should().BeEquivalentTo(OpenWorldTools,
            "open-world means the tool reaches past this machine or runs arbitrary code; "
            + "driving a local desktop app is closed-world");
    }

    /// <summary>C-7 R2 / R10: the Destructive column, pinned literally as the table demands.</summary>
    [Fact]
    public void The_destructive_set_is_the_tables_destructive_set()
    {
        ToolsWith("Destructive").Should().BeEquivalentTo(DestructiveTools,
            "clients confirm on destructiveHint; the set is a reviewed table, not per-file judgement");
    }

    /// <summary>C-7 R2 / R10: the Idempotent column.</summary>
    [Fact]
    public void The_idempotent_set_is_the_tables_idempotent_set()
    {
        ToolsWith("Idempotent").Should().BeEquivalentTo(IdempotentTools,
            "idempotent says a repeat leaves the same state - a second type or a second "
            + "scheduled_task(create) does not");
    }

    /// <summary>C-7 R2: the two hints that cannot both be true of one tool.</summary>
    [Fact]
    public void No_tool_is_both_read_only_and_destructive()
    {
        ToolsWith("ReadOnly").Intersect(ToolsWith("Destructive")).Should().BeEmpty(
            "a tool that changes nothing cannot also destroy something; one of the two hints is wrong");
    }

    /// <summary>C-7 R2: reading twice always leaves the same state.</summary>
    [Fact]
    public void Every_read_only_tool_is_idempotent()
    {
        ToolsWith("ReadOnly").Except(ToolsWith("Idempotent")).Should().BeEmpty(
            "a read leaves the state it found, so repeating it is idempotent by definition");
    }

    // ---- C-7 R3: README's Safety rails section is the destructive floor -----------------------

    /// <summary>The "## Safety rails" section of README.md, up to the next heading.</summary>
    private static string SafetyRailsSection()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md")).ReplaceLineEndings("\n");
        int start = readme.IndexOf("## Safety rails", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, "README.md advertises the confirm-gated tools under that heading");
        int end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    /// <summary>
    /// The tool names in the section's bullet list - the tools README tells the reader are gated
    /// behind <c>confirm: true</c>. Only that list: the prose below it also backticks
    /// <c>scrape</c> and <c>env(get|list)</c>, which are reads.
    /// </summary>
    private static string[] SafetyRailToolNames()
    {
        var bullets = new List<string>();
        bool started = false;
        foreach (var line in SafetyRailsSection().Split('\n'))
        {
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal)) { started = true; bullets.Add(line); continue; }
            if (!started) continue;
            if (line.StartsWith("  ", StringComparison.Ordinal) && line.Trim().Length > 0) { bullets.Add(line); continue; }
            break;   // the confirm-gated list ends at the first line that is not part of a bullet
        }

        return Regex.Matches(string.Join("\n", bullets), "`([^`]+)`")
            .Select(m => m.Groups[1].Value.Split('(')[0].Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void The_safety_rails_list_names_tools_this_assembly_has()
    {
        // Guards the parse itself: a rename or a reformat that made the sweep below vacuous would
        // fail here instead of passing silently.
        var names = SafetyRailToolNames();
        var wire = ToolMethods().Select(m => m.Name.Replace("_", "")).ToArray();

        names.Should().HaveCountGreaterThanOrEqualTo(9, "README lists the confirm-gated tools one per bullet");
        names.Should().Contain("file_write").And.Contain("file_manage").And.Contain("registry_set");
        names.Where(n => !wire.Any(w => w.Equals(n.Replace("_", ""), StringComparison.OrdinalIgnoreCase)))
            .Should().BeEmpty("every name in README's safety rails list is a tool this assembly exposes");
    }

    [Fact]
    public void Every_tool_readme_calls_destructive_carries_the_destructive_hint()
    {
        var destructive = ToolsWith("Destructive").Select(n => n.Replace("_", "")).ToArray();

        var unhinted = SafetyRailToolNames()
            .Where(n => !destructive.Any(d => d.Equals(n.Replace("_", ""), StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        unhinted.Should().BeEmpty(
            "README tells a human these tools need confirmation; destructiveHint is the same "
            + "statement to the client, and the two must not disagree");
    }

    /// <summary>
    /// C-2 (R5): the new tool joins the documented safety rails. docs-agent owns the edit; this is
    /// what tells it to.
    /// </summary>
    [Fact]
    public void The_documents_name_registry_delete_among_the_destructive_tools()
    {
        SafetyRailToolNames().Should().Contain("registry_delete",
            "registry_delete removes keys behind confirm: true - run docs-agent");
        Skill().Should().Contain("registry_delete",
            "the skill playbook lists the registry verbs and the confirm-gated tools - run docs-agent");
    }

    /// <summary>
    /// C-1 R4: the file tools' safer defaults are a behaviour change a model only survives if the
    /// playbook names the flag to pass. docs-agent owns the edit; this is what tells it to.
    /// </summary>
    [Fact]
    public void The_skill_names_the_new_file_flags()
    {
        var skill = Skill();

        skill.Should().Contain("overwrite",
            "copy/move now refuse an existing destination - the playbook has to name the flag - run docs-agent");
        skill.Should().Contain("offset_lines",
            "a 5 MB log is readable a window at a time only if the playbook says so - run docs-agent");
    }
}

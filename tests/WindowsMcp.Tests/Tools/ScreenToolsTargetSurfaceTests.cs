using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-7: the part of the change the model reads before it calls anything — the parameter lists,
/// their defaults, the wire schema and the descriptions. "Appended after their existing ones" keeps
/// every positional caller working; the descriptions are the only place the model learns that a
/// target capture is the screen rectangle the target occupies (a window over it is in the
/// picture), and that json word centres go straight to <c>click</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ScreenToolsTargetSurfaceTests
{
    private static MethodInfo Method(string name) => typeof(ScreenTools).GetMethod(name)!;

    private static ParameterInfo Parameter(string method, string name) =>
        Method(method).GetParameters().Single(p => p.Name == name);

    private static string? ParameterDescription(string method, string name) =>
        Parameter(method, name).GetCustomAttribute<DescriptionAttribute>()?.Description;

    private static string ToolDescription(string method) =>
        Method(method).GetCustomAttribute<DescriptionAttribute>()!.Description;

    /// <summary>The tool description plus the three target parameters' descriptions: everything the model reads about targets.</summary>
    private static string TargetText(string method) =>
        string.Join("\n", new[] { ToolDescription(method) }
            .Concat(new[] { "window", "hwnd", "element_id" }.Select(p => ParameterDescription(method, p) ?? "")));

    private static Tool ProtocolTool(MethodInfo method) =>
        McpServerTool.Create(method, (RequestContext<CallToolRequestParams> _) =>
            throw new InvalidOperationException("metadata only - the tool is never invoked")).ProtocolTool;

    // ---- R1 / R45 — the parameter lists ---------------------------------------------------------

    [Fact]
    public void Screenshot_appends_window_hwnd_and_element_id_after_backend()
    {
        Method(nameof(ScreenTools.Screenshot)).GetParameters().Select(p => p.Name).Should().Equal(
            "region", "display", "format", "output", "max_width", "max_height", "scale", "quality",
            "include_cursor", "annotate", "grid_columns", "grid_rows", "backend",
            "window", "hwnd", "element_id");
    }

    [Fact]
    public void Ocr_appends_window_hwnd_element_id_and_format_after_display()
    {
        // Ambiguity resolved: the note says the three targets are appended to both tools and that
        // ocr "gains format"; format follows the targets so the two tools' target blocks line up.
        Method(nameof(ScreenTools.Ocr)).GetParameters().Select(p => p.Name).Should().Equal(
            "region", "display", "window", "hwnd", "element_id", "format");
    }

    [Theory]
    [InlineData(nameof(ScreenTools.Screenshot))]
    [InlineData(nameof(ScreenTools.Ocr))]
    public void The_target_parameters_are_optional_with_null_defaults_and_the_documented_types(string method)
    {
        var window = Parameter(method, "window");
        var hwnd = Parameter(method, "hwnd");
        var elementId = Parameter(method, "element_id");

        window.ParameterType.Should().Be(typeof(string));
        hwnd.ParameterType.Should().Be(typeof(long?), "an HWND from window list is a 64-bit handle");
        elementId.ParameterType.Should().Be(typeof(string));
        foreach (var p in new[] { window, hwnd, elementId })
        {
            p.HasDefaultValue.Should().BeTrue($"'{p.Name}' is optional");
            p.DefaultValue.Should().BeNull($"'{p.Name}' defaults to 'no target'");
        }
    }

    [Fact]
    public void Ocr_format_defaults_to_text()
    {
        var format = Parameter(nameof(ScreenTools.Ocr), "format");

        format.ParameterType.Should().Be(typeof(string));
        format.HasDefaultValue.Should().BeTrue();
        format.DefaultValue.Should().Be("text", "the text mode is today's ocr, so it is the default");
    }

    [Theory]
    [InlineData(nameof(ScreenTools.Screenshot))]
    [InlineData(nameof(ScreenTools.Ocr))]
    public void The_wire_schema_offers_the_new_parameters_and_requires_none_of_them(string method)
    {
        var schema = ProtocolTool(Method(method)).InputSchema;
        var properties = schema.GetProperty("properties");
        var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.GetString()).ToArray()
            : [];

        var added = method == nameof(ScreenTools.Ocr)
            ? new[] { "window", "hwnd", "element_id", "format" }
            : new[] { "window", "hwnd", "element_id" };
        foreach (var name in added)
        {
            properties.TryGetProperty(name, out _).Should().BeTrue($"'{name}' is on the wire");
            required.Should().NotContain(name, $"'{name}' is optional");
        }
    }

    // ---- R46 — descriptions ---------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(ScreenTools.Screenshot), "window")]
    [InlineData(nameof(ScreenTools.Screenshot), "hwnd")]
    [InlineData(nameof(ScreenTools.Screenshot), "element_id")]
    [InlineData(nameof(ScreenTools.Ocr), "window")]
    [InlineData(nameof(ScreenTools.Ocr), "hwnd")]
    [InlineData(nameof(ScreenTools.Ocr), "element_id")]
    [InlineData(nameof(ScreenTools.Ocr), "format")]
    public void Every_new_parameter_carries_a_description(string method, string parameter)
    {
        ParameterDescription(method, parameter).Should().NotBeNullOrWhiteSpace(
            "an argument with no description is invisible in the wire schema");
    }

    [Theory]
    [InlineData("window")]
    [InlineData("hwnd")]
    [InlineData("element_id")]
    public void Screenshot_and_ocr_advertise_the_same_text_for_the_target_parameters(string parameter)
    {
        // One resolver: two descriptions that drift would tell the model the tools differ. (Two
        // missing descriptions are "equal" too, so presence is asserted first.) Review F7 lets
        // screenshot's element_id ADD the annotate caveat, which means nothing to ocr: screenshot's
        // text must contain ocr's, not equal it.
        var screenshot = ParameterDescription(nameof(ScreenTools.Screenshot), parameter);
        var ocr = ParameterDescription(nameof(ScreenTools.Ocr), parameter);
        screenshot.Should().NotBeNullOrWhiteSpace();
        ocr.Should().NotBeNullOrWhiteSpace();
        screenshot.Should().Contain(ocr!);
    }

    // ---- E-7 review F3/F6/F7 and the tilted-text decision ---------------------------------------
    //
    // Each rule needs ONE sentence that says it, so the words cannot be satisfied by unrelated
    // parts of a long description. A sentence ends at '.', ';' or ':' followed by whitespace.

    private static IEnumerable<string> Sentences(params string?[] texts) =>
        texts.Where(t => t is not null)
            .SelectMany(t => System.Text.RegularExpressions.Regex.Split(t!, @"(?<=[.;:])\s+"));

    private static bool AnySentence(IEnumerable<string> sentences, params string[] patterns) =>
        sentences.Any(s => patterns.All(p => System.Text.RegularExpressions.Regex.IsMatch(s, p,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)));

    [Fact]
    public void The_ocr_text_says_to_narrow_json_on_a_dense_screen()
    {
        // Required in one sentence: (dense|large|big|lot|full|huge|long) + (narrow|limit|restrict|smaller)
        // + (window|element_id|region).
        var sentences = Sentences(ToolDescription(nameof(ScreenTools.Ocr)), ParameterDescription(nameof(ScreenTools.Ocr), "format"));

        AnySentence(sentences, @"\b(dense|large|big|lot|full|huge|long)\b", @"\b(narrow|limit|restrict|smaller)", @"\b(window|element_id|region)\b")
            .Should().BeTrue("format:json over a whole dense screen can be very large; the model must be told to narrow it");
    }

    [Theory]
    [InlineData(nameof(ScreenTools.Screenshot))]
    [InlineData(nameof(ScreenTools.Ocr))]
    public void Both_tools_say_a_maximised_window_normally_reports_targetClipped(string method)
    {
        // Required in one sentence: maximi[sz]ed + targetClipped (the frame / overhang reason is welcome, not required).
        var sentences = Sentences(ToolDescription(method), ParameterDescription(method, "window"));

        AnySentence(sentences, @"maximi[sz]", @"targetClipped")
            .Should().BeTrue("otherwise the model reads targetClipped on every maximised window as something wrong");
    }

    [Fact]
    public void The_screenshot_element_id_text_says_annotate_replaces_the_ids()
    {
        // Required in one sentence of screenshot's element_id description: annotate +
        // (replac|evict|expire|invalidat|no longer|stale|new ids).
        var sentences = Sentences(ParameterDescription(nameof(ScreenTools.Screenshot), "element_id"));

        AnySentence(sentences, @"annotate", @"(replac|evict|expire|invalidat|no longer|stale|new ids)")
            .Should().BeTrue("annotate's walk mints new ids: the one passed is not valid afterwards");
    }

    [Fact]
    public void The_screenshot_element_id_annotate_caveat_is_limited_to_snapshot_ids_and_precedes_the_default()
    {
        // E-7 review round 3 (R2): only an id from an earlier snapshot (el_N) is replaced by
        // annotate's walk. Required in one sentence: annotate + el_ + snapshot. And the description
        // ends with its "Default: none" — nothing is appended after the default.
        var text = ParameterDescription(nameof(ScreenTools.Screenshot), "element_id") ?? "";

        AnySentence(Sentences(text), @"annotate", @"el_", @"snapshot")
            .Should().BeTrue("the 'replaced' claim is about snapshot ids (el_N), not every element id");
        text.Should().MatchRegex(@"Default: none\.?\s*$", "the default is the last thing the description says");
    }

    [Fact]
    public void The_ocr_text_says_tilted_boxes_are_upright_and_larger_but_centres_stay_the_word_centres()
    {
        // E-7 review round 3 (R1, R4) — replaces "positions are approximate when textAngle is
        // present": for tilted text each rectangle is the upright box around the tilted word
        // (larger than the word), while centerX/centerY are still the word's centre.
        // Sentence 1 requires: textAngle + (upright|axis-aligned|enclos|bounding) + (larger|bigger|loose|extend).
        // Sentence 2 requires: centerX + a standalone \bcent(re|er)s?\b + (textAngle|tilt|rotat).
        var sentences = Sentences(ToolDescription(nameof(ScreenTools.Ocr)), ParameterDescription(nameof(ScreenTools.Ocr), "format")).ToArray();

        AnySentence(sentences, @"textAngle", @"(upright|axis-aligned|enclos|bounding)", @"(larger|bigger|loose|extend)")
            .Should().BeTrue("the boxes of tilted words are upright and bigger than the words; the model must not read them as tight");
        AnySentence(sentences, @"centerX", @"\bcent(re|er)s?\b", @"(textAngle|tilt|rotat)")
            .Should().BeTrue("but the centres are still the words' centres, so a click there lands on the word");
    }

    [Fact]
    public void The_window_description_says_how_a_title_is_matched()
    {
        var text = ParameterDescription(nameof(ScreenTools.Screenshot), "window") ?? "";

        text.Should().Contain("title").And.Contain("fuzzy", "exact -> substring -> fuzzy is the B-10 ladder");
    }

    [Fact]
    public void The_hwnd_description_says_where_a_handle_comes_from_and_that_it_wins()
    {
        var text = ParameterDescription(nameof(ScreenTools.Screenshot), "hwnd") ?? "";

        text.Should().Contain("window").And.Contain("list", "handles come from window(action:\"list\")")
            .And.Contain("wins", "with window too, hwnd wins (B-10)");
    }

    [Fact]
    public void The_element_id_description_names_the_ids_it_takes()
    {
        var text = ParameterDescription(nameof(ScreenTools.Screenshot), "element_id") ?? "";

        text.Should().Contain("el_", "the snapshot's ids").And.Contain("find_element");
    }

    [Theory]
    [InlineData(nameof(ScreenTools.Screenshot))]
    [InlineData(nameof(ScreenTools.Ocr))]
    public void The_descriptions_say_a_target_capture_is_the_screen_rectangle_and_to_bring_the_window_forward(string method)
    {
        var text = TargetText(method);

        text.Should().Contain("window").And.Contain("hwnd").And.Contain("element_id");
        text.Should().Contain("switch_to_window", "a window lying over the target is in the picture; the model must know to raise it first");
        text.ToLowerInvariant().Should().Contain("rectangle", "what is captured is the screen rectangle the target occupies");
    }

    [Fact]
    public void The_ocr_description_documents_format_json_and_clickable_word_centres()
    {
        var tool = ToolDescription(nameof(ScreenTools.Ocr));
        var format = ParameterDescription(nameof(ScreenTools.Ocr), "format") ?? "";
        var all = tool + "\n" + format;

        format.Should().Contain("text").And.Contain("json");
        all.Should().Contain("centerX").And.Contain("virtual-desktop")
            .And.Contain("click", "the centres go straight to click: that is the point of json");
    }

    [Fact]
    public void The_screenshot_description_documents_the_target_metadata()
    {
        var tool = ToolDescription(nameof(ScreenTools.Screenshot));

        tool.Should().Contain("target", "the metadata list is the only place the model learns the field exists")
            .And.Contain("targetClipped");
    }

    // ---- R47 — annotations unchanged ----------------------------------------------------------------

    [Theory]
    [InlineData(nameof(ScreenTools.Screenshot))]
    [InlineData(nameof(ScreenTools.Ocr))]
    public void Both_tools_stay_read_only_idempotent_closed_world_and_non_destructive(string method)
    {
        var named = Method(method).GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(McpServerToolAttribute))
            .NamedArguments.ToDictionary(a => a.MemberName, a => a.TypedValue.Value);

        named["ReadOnly"].Should().Be(true, "nothing is focused, raised, restored or scrolled");
        named["Destructive"].Should().Be(false);
        named["Idempotent"].Should().Be(true);
        named["OpenWorld"].Should().Be(false);
    }
}

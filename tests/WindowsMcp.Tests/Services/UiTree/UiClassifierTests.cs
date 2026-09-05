using FlaUI.Core.Definitions;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Services.UiTree;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.NodeFixtures;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// A-2 (R3): the whole classification contract on hand-written nodes - what counts as
/// interactive, what you can do to it, where its centre is. This is the D-6 acceptance test
/// carried over ("every type in upstream's INTERACTIVE_CONTROL_TYPE_NAMES is interactive, and
/// find_element calls this classifier instead of keeping its own list") plus the LegacyIAccessible
/// role fallback D-6 deferred to here. No UIA, no desktop (roadmap C10).
/// </summary>
[Trait("Category", "Unit")]
public class UiClassifierTests
{
    /// <summary>Upstream INTERACTIVE_CONTROL_TYPE_NAMES + Document (D-6), in the order the array declares them.</summary>
    private static readonly string[] Interactive17 =
    [
        "Button", "ListItem", "MenuItem", "Edit", "CheckBox", "RadioButton", "ComboBox", "Hyperlink",
        "SplitButton", "TabItem", "TreeItem", "DataItem", "HeaderItem", "Spinner", "Slider", "ScrollBar",
        "Document",
    ];

    // ---- R3.1 the shared set: one home, and find_element uses it ------------------------------

    [Fact]
    public void InteractiveControlTypes_is_still_the_D6_set_of_seventeen()
        => UiClassifier.InteractiveControlTypes.Should().Equal(
            ControlType.Button, ControlType.ListItem, ControlType.MenuItem, ControlType.Edit,
            ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Hyperlink,
            ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
            ControlType.HeaderItem, ControlType.Spinner, ControlType.Slider, ControlType.ScrollBar,
            ControlType.Document);

    [Fact]
    public void UIAutomationService_find_path_uses_the_classifier_set_not_a_copy_of_it()
        // D-6 left the array in the service "so A-2 can take it over without the two drifting
        // apart". Sequence equality would pass over two copies that drift the day after; only the
        // SAME array proves the move happened.
        => UIAutomationService.InteractiveControlTypes.Should().BeSameAs(UiClassifier.InteractiveControlTypes);

    [Fact]
    public void InteractiveControlTypeNames_are_the_same_seventeen_in_the_same_order()
    {
        UiClassifier.InteractiveControlTypeNames.Should().Equal(Interactive17);
        UiClassifier.InteractiveControlTypeNames.Should().Equal(
            UiClassifier.InteractiveControlTypes.Select(t => t.ToString()),
            "the names are what a UiNode carries; they must be the names of the enum set, not a second list");
    }

    [Fact]
    public void InformativeControlTypes_are_the_six_read_only_types_and_exclude_HeaderItem()
    {
        UiClassifier.InformativeControlTypes.Should().BeEquivalentTo(
            new[] { "Text", "Image", "StatusBar", "ProgressBar", "ToolTip", "Header" });
        UiClassifier.InformativeControlTypes.Should().NotContain("HeaderItem",
            "a column header is clickable (it sorts) - HeaderItem is interactive, Header is the container");
    }

    [Fact]
    public void InteractiveLegacyRoles_are_the_MSAA_roles_and_match_case_insensitively()
    {
        UiClassifier.InteractiveLegacyRoles.Should().BeEquivalentTo(new[]
        {
            "pushbutton", "checkbutton", "radiobutton", "combobox", "link", "menuitem", "listitem",
            "pagetab", "slider", "spinbutton", "outlineitem", "cell", "splitbutton", "buttondropdown",
            "buttonmenu", "text",
        });
        UiClassifier.InteractiveLegacyRoles.Contains("PushButton").Should().BeTrue(
            "MSAA role names arrive in whatever case the provider chose");
    }

    // ---- R3.2 Classify ------------------------------------------------------------------------

    [Theory]
    [InlineData("Button")]
    [InlineData("ListItem")]
    [InlineData("MenuItem")]
    [InlineData("Edit")]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    [InlineData("ComboBox")]
    [InlineData("Hyperlink")]
    [InlineData("SplitButton")]
    [InlineData("TabItem")]
    [InlineData("TreeItem")]
    [InlineData("DataItem")]
    [InlineData("HeaderItem")]
    [InlineData("Spinner")]
    [InlineData("Slider")]
    [InlineData("ScrollBar")]
    [InlineData("Document")]
    public void Classify_every_interactive_control_type_is_interactive(string controlType)
        => UiClassifier.Classify(Node(controlType: controlType)).Should().Be(UiRole.Interactive);

    [Theory]
    [InlineData("Text")]
    [InlineData("Image")]
    [InlineData("StatusBar")]
    [InlineData("ProgressBar")]
    [InlineData("ToolTip")]
    [InlineData("Header")]
    public void Classify_read_only_types_are_informative(string controlType)
        => UiClassifier.Classify(Node(controlType: controlType)).Should().Be(UiRole.Informative);

    [Theory]
    [InlineData("Pane")]
    [InlineData("Window")]
    [InlineData("Group")]
    [InlineData("Custom")]
    [InlineData("TitleBar")]
    [InlineData("ToolBar")]
    [InlineData("Menu")]
    [InlineData("List")]
    [InlineData("Tree")]
    [InlineData("Table")]
    public void Classify_containers_are_structural(string controlType)
        => UiClassifier.Classify(Node(controlType: controlType)).Should().Be(UiRole.Structural);

    [Theory]
    [InlineData("pushbutton")]
    [InlineData("checkbutton")]
    [InlineData("radiobutton")]
    [InlineData("combobox")]
    [InlineData("link")]
    [InlineData("menuitem")]
    [InlineData("listitem")]
    [InlineData("pagetab")]
    [InlineData("slider")]
    [InlineData("spinbutton")]
    [InlineData("outlineitem")]
    [InlineData("cell")]
    [InlineData("splitbutton")]
    [InlineData("buttondropdown")]
    [InlineData("buttonmenu")]
    public void Classify_a_custom_element_with_an_interactive_legacy_role_is_interactive(string role)
        // Chromium and Qt report Custom for almost everything; the MSAA role is the only thing
        // that says a Custom is a button.
        => UiClassifier.Classify(Node(controlType: "Custom", legacyRole: role)).Should().Be(UiRole.Interactive);

    [Theory]
    [InlineData("PushButton")]
    [InlineData("PUSHBUTTON")]
    [InlineData("pushButton")]
    public void Classify_matches_the_legacy_role_case_insensitively(string role)
        => UiClassifier.Classify(Node(controlType: "Custom", legacyRole: role)).Should().Be(UiRole.Interactive);

    [Fact]
    public void Classify_a_custom_element_with_an_unlisted_legacy_role_is_structural()
        => UiClassifier.Classify(Node(controlType: "Custom", legacyRole: "graphic")).Should().Be(UiRole.Structural);

    [Fact]
    public void Classify_role_text_without_a_value_is_not_interactive()
        // ROLE_SYSTEM_TEXT covers BOTH an edit box and a static label. Only the one with a
        // ValuePattern can be typed into; treating the label as interactive is how a snapshot
        // fills up with unclickable noise.
        => UiClassifier.Classify(Node(controlType: "Custom", legacyRole: "text", value: null)).Should().Be(UiRole.Structural);

    [Fact]
    public void Classify_role_text_with_a_value_is_interactive()
        => UiClassifier.Classify(Node(controlType: "Custom", legacyRole: "text", value: "x")).Should().Be(UiRole.Interactive);

    [Fact]
    public void Classify_role_text_with_an_empty_value_is_interactive()
        // An empty edit box still has a ValuePattern; "" is a value, absent is not.
        => UiClassifier.Classify(Node(controlType: "Custom", legacyRole: "text", value: "")).Should().Be(UiRole.Interactive);

    [Fact]
    public void Classify_an_informative_type_with_an_interactive_role_is_interactive()
        // Order matters: control type, then role, then informative. A Text that MSAA calls a link
        // is a link.
        => UiClassifier.Classify(Node(controlType: "Text", legacyRole: "link")).Should().Be(UiRole.Interactive);

    [Fact]
    public void Classify_an_interactive_type_is_interactive_whatever_the_role_says()
        => UiClassifier.Classify(Node(controlType: "Button", legacyRole: "graphic")).Should().Be(UiRole.Interactive);

    [Fact]
    public void Classify_an_unknown_control_type_is_structural()
        => UiClassifier.Classify(Node(controlType: "SemanticZoom")).Should().Be(UiRole.Structural);

    // ---- R3.3 ActionFor -----------------------------------------------------------------------

    [Theory]
    [InlineData("Edit", "fill")]
    [InlineData("Document", "fill")]
    [InlineData("CheckBox", "toggle")]
    [InlineData("ComboBox", "select")]
    [InlineData("Slider", "slide")]
    [InlineData("Spinner", "slide")]
    [InlineData("ScrollBar", "scroll")]
    [InlineData("Button", "click")]
    [InlineData("RadioButton", "click")]
    [InlineData("Hyperlink", "click")]
    [InlineData("MenuItem", "click")]
    [InlineData("ListItem", "click")]
    [InlineData("TabItem", "click")]
    [InlineData("TreeItem", "click")]
    [InlineData("DataItem", "click")]
    [InlineData("HeaderItem", "click")]
    [InlineData("SplitButton", "click")]
    public void ActionFor_maps_each_interactive_control_type(string controlType, string action)
        => UiClassifier.ActionFor(Node(controlType: controlType)).Should().Be(action);

    [Theory]
    [InlineData("checkbutton", "toggle")]
    [InlineData("combobox", "select")]
    [InlineData("slider", "slide")]
    [InlineData("spinbutton", "slide")]
    [InlineData("text", "fill")]
    [InlineData("pushbutton", "click")]
    [InlineData("link", "click")]
    [InlineData("menuitem", "click")]
    [InlineData("listitem", "click")]
    [InlineData("cell", "click")]
    public void ActionFor_maps_a_custom_element_by_its_legacy_role(string role, string action)
        => UiClassifier.ActionFor(Node(controlType: "Custom", legacyRole: role, value: "x")).Should().Be(action);

    [Fact]
    public void ActionFor_prefers_the_control_type_over_the_legacy_role()
        => UiClassifier.ActionFor(Node(controlType: "Edit", legacyRole: "pushbutton")).Should().Be("fill");

    [Fact]
    public void ActionFor_maps_an_uppercase_legacy_role_the_same_way()
        => UiClassifier.ActionFor(Node(controlType: "Custom", legacyRole: "CheckButton")).Should().Be("toggle");

    [Theory]
    [InlineData("Pane")]
    [InlineData("Group")]
    [InlineData("Custom")]
    [InlineData("Text")]
    [InlineData("SemanticZoom")]
    public void ActionFor_a_node_that_is_not_interactive_at_all_still_answers_click(string controlType)
        // Classify calls these structural or informative, so the traverser never asks what to do
        // with one. The answer is pinned anyway: it is the harmless default a future caller would
        // get, and nothing else in the suite reaches the null-role arm of the switch.
        => UiClassifier.ActionFor(Node(controlType: controlType, legacyRole: null)).Should().Be("click");

    [Fact]
    public void ActionFor_an_unlisted_legacy_role_falls_through_to_click()
        // A role the MSAA table does not name is still something the model can try clicking.
        => UiClassifier.ActionFor(Node(controlType: "Custom", legacyRole: "graphic")).Should().Be("click");

    // ---- R3.4 IsScrollable --------------------------------------------------------------------

    [Fact]
    public void IsScrollable_is_false_without_a_scroll_pattern()
        => UiClassifier.IsScrollable(Node(controlType: "Document", scroll: null)).Should().BeFalse();

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void IsScrollable_needs_at_least_one_scrollable_axis(bool vertical, bool horizontal, bool expected)
        // A ScrollPattern is present on plenty of panes that cannot actually scroll; the flags,
        // not the pattern, decide whether the element belongs in the scrollable list.
        => UiClassifier.IsScrollable(Node(controlType: "Document", scroll: new ScrollInfo(0, 0, vertical, horizontal)))
            .Should().Be(expected);

    // ---- R3.5 CenterOf ------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 100, 800, 600, 500, 400)]
    [InlineData(0, 0, 3, 3, 1, 1)]                // odd sizes floor; the point stays inside the box
    [InlineData(-1920, 0, 1920, 1080, -960, 540)] // a monitor left of the primary (roadmap C1)
    [InlineData(600, 380, 24, 16, 612, 388)]
    [InlineData(7, 9, 0, 0, 7, 9)]                // zero-area: the origin, not a crash
    public void CenterOf_is_the_middle_of_the_bounds(int x, int y, int w, int h, int cx, int cy)
        => UiClassifier.CenterOf(new Bounds(x, y, w, h)).Should().Be((cx, cy));

    // ---- R3.6 ShortcutOf ----------------------------------------------------------------------

    [Fact]
    public void ShortcutOf_prefers_the_accelerator_over_the_access_key()
        // Ctrl+S is what a person would press; Alt+F is only live while the menu is open.
        => UiClassifier.ShortcutOf(Node(acceleratorKey: "Ctrl+S", accessKey: "Alt+F")).Should().Be("Ctrl+S");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShortcutOf_falls_back_to_the_access_key_when_the_accelerator_is_blank(string? accelerator)
        => UiClassifier.ShortcutOf(Node(acceleratorKey: accelerator, accessKey: "Alt+F")).Should().Be("Alt+F");

    [Fact]
    public void ShortcutOf_trims_what_it_returns()
        => UiClassifier.ShortcutOf(Node(acceleratorKey: "  Ctrl+S  ")).Should().Be("Ctrl+S");

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", null)]
    public void ShortcutOf_is_null_when_the_element_advertises_no_shortcut(string? accelerator, string? access)
        => UiClassifier.ShortcutOf(Node(acceleratorKey: accelerator, accessKey: access)).Should().BeNull();

    // ---- UIAutomationService.Project (pure projection of one node) ---------------------------

    [Fact]
    public void Project_never_carries_a_password_fields_value_even_into_json()
    {
        var node = NodeFixtures.Node("Edit") with { IsPassword = true, Value = "hunter2", Bounds = new Bounds(0, 0, 10, 10) };

        var (element, _) = WindowsMcp.Services.UIAutomationService.Project(node, "el_1");

        element.Should().NotBeNull();
        element!.IsPassword.Should().BeTrue();
        element.Value.Should().BeNull("the renderer's [password] tag is one code path; format:json is another, and neither may leak");
    }

    [Fact]
    public void Project_puts_an_interactive_scrollable_node_in_both_lists_with_one_id()
    {
        var node = NodeFixtures.Node("Document") with
        {
            Bounds = new Bounds(0, 0, 100, 50),
            Scroll = new ScrollInfo(37, 0, true, false),
        };

        var (element, region) = WindowsMcp.Services.UIAutomationService.Project(node, "el_7");

        element!.ElementId.Should().Be("el_7");
        region!.ElementId.Should().Be("el_7");
        element.CenterX.Should().Be(50);
        region.Scroll.VerticalPercent.Should().Be(37);
    }

    [Fact]
    public void Project_returns_nothing_for_a_node_without_bounds()
    {
        var node = NodeFixtures.Node("Button") with { Bounds = null };

        WindowsMcp.Services.UIAutomationService.Project(node, "el_1").Should().Be(((SnapshotElement?)null, (SnapshotScrollable?)null));
    }
}

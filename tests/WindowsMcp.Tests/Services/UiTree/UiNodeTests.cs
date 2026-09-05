using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// A-2 (R2): <see cref="UiNode"/> is one row of pure data. The only contract it has is that a
/// traverser which could read nothing but the control type can still build one — every optional
/// field is genuinely optional, because a guarded UIA read (D-5) returns null far more often than
/// the happy path suggests.
/// </summary>
[Trait("Category", "Unit")]
public class UiNodeTests
{
    [Fact]
    public void UiNode_is_constructible_with_every_optional_field_null()
    {
        var node = new UiNode(
            Window: "Untitled - Notepad", ControlType: "Custom", Name: "", Bounds: null,
            IsEnabled: false, IsOffscreen: true, HasFocus: false, IsPassword: false,
            Value: null, RangeValue: null, RangeMin: null, RangeMax: null,
            ToggleState: null, ExpandState: null, AccessKey: null, AcceleratorKey: null,
            LegacyRole: null, Scroll: null, Depth: 0);

        node.Bounds.Should().BeNull();
        node.Value.Should().BeNull();
        node.Scroll.Should().BeNull();
        node.Depth.Should().Be(0);
    }

    [Fact]
    public void UiNode_carries_every_fact_the_snapshot_reports()
    {
        var scroll = new ScrollInfo(37, 0, true, false);
        var node = NodeFixtures.Node(
            controlType: "Slider", name: "Volume", window: "Mixer", bounds: new Bounds(10, 20, 30, 40),
            hasFocus: true, isPassword: true, value: "hello", rangeValue: 30, rangeMin: 0, rangeMax: 100,
            toggleState: "On", expandState: "Collapsed", accessKey: "Alt+V", acceleratorKey: "Ctrl+S",
            legacyRole: "slider", scroll: scroll, depth: 3);

        node.Should().BeEquivalentTo(new UiNode("Mixer", "Slider", "Volume", new Bounds(10, 20, 30, 40),
            true, false, true, true, "hello", 30, 0, 100, "On", "Collapsed", "Alt+V", "Ctrl+S", "slider", scroll, 3));
    }
}

using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// B-3 (R6): <c>scroll</c> with optional coordinates. Upstream's <c>Scroll()</c> scrolls wherever
/// the pointer already is; ours demanded a point, so an agent had to invent one. The tests pin the
/// new parameter ORDER (direction first - a positional break called out in CHANGELOG), the
/// cursor fallback (roadmap C2) and the Shift+wheel horizontal path.
/// </summary>
[Trait("Category", "Unit")]
public class InputToolsScrollTests
{
    private static Mock<IInputService> ScrollingInput(int cursorX = 7, int cursorY = 9)
    {
        var input = new Mock<IInputService>();
        input.Setup(s => s.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(cursorX, cursorY));
        return input;
    }

    // ---- where it scrolls ---------------------------------------------------------------------

    [Fact]
    public async Task Scroll_with_no_target_scrolls_under_the_live_cursor_and_says_so()
    {
        var input = ScrollingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Scroll("down"));

        input.Verify(s => s.GetCursorPositionAsync(It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.ScrollAsync(7, 9, "down", 3, false, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "target").Should().Be("cursor");
        InputVerb.Num(json, "x").Should().Be(7, "a call with nothing given is still deterministic and reported");
        InputVerb.Num(json, "y").Should().Be(9);
        InputVerb.Str(json, "direction").Should().Be("down");
        InputVerb.Num(json, "amount").Should().Be(3, "3 is the documented default");
        InputVerb.Flag(json, "shiftWheel").Should().BeFalse();
        InputVerb.Absent(json, "elementId").Should().BeTrue();
    }

    [Fact]
    public async Task Scroll_at_coordinates_scrolls_there_and_never_reads_the_cursor()
    {
        var input = ScrollingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Scroll("up", amount: 5, x: 400, y: 300));

        input.Verify(s => s.ScrollAsync(400, 300, "up", 5, false, It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.GetCursorPositionAsync(It.IsAny<CancellationToken>()), Times.Never);
        InputVerb.Str(json, "target").Should().Be("point");
        InputVerb.Num(json, "amount").Should().Be(5);
    }

    [Fact]
    public async Task Scroll_by_element_id_scrolls_at_its_centre_and_reports_the_element()
    {
        var input = ScrollingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_20", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_20", name: "Results list"));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia).Scroll("down", element_id: "el_20"));

        input.Verify(s => s.ScrollAsync(120, 210, "down", 3, false, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "target").Should().Be("element");
        InputVerb.Str(json, "elementId").Should().Be("el_20");
        InputVerb.Str(json, "name").Should().Be("Results list");
    }

    [Fact]
    public async Task Scroll_refuses_an_offscreen_element()
    {
        var input = ScrollingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_5", offscreen: true));

        var act = () => InputVerb.Tools(input, uia).Scroll("down", element_id: "el_5");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_5").And.Contain("off-screen");
    }

    [Fact]
    public async Task Scroll_with_both_coordinates_and_an_element_id_is_refused()
    {
        var input = ScrollingInput();

        var act = () => InputVerb.Tools(input).Scroll("down", x: 1, y: 2, element_id: "el_20");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("element_id").And.Contain("coordinates");
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(5, null)]
    [InlineData(null, 5)]
    public async Task Scroll_with_half_a_coordinate_pair_is_refused(int? x, int? y)
    {
        // Silently falling back to the cursor here would scroll somewhere the caller half-named.
        var input = ScrollingInput();

        var act = () => InputVerb.Tools(input).Scroll("down", x: x, y: y);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("x").And.Contain("y");
        input.VerifyNoOtherCalls();
    }

    // ---- shift_wheel: the horizontal scroll for apps with no horizontal wheel ------------------

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("LEFT")]
    public async Task Shift_wheel_is_forwarded_for_a_horizontal_direction(string direction)
    {
        var input = ScrollingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Scroll(direction, x: 1, y: 2, shift_wheel: true));

        input.Verify(s => s.ScrollAsync(1, 2, direction, 3, true, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Flag(json, "shiftWheel").Should().BeTrue();
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("UP")]
    public async Task Shift_wheel_is_refused_for_a_vertical_direction(string direction)
    {
        // Shift+wheel IS the vertical wheel with Shift held; asking for it with up/down would
        // scroll sideways instead, which is the opposite of what was requested.
        var input = ScrollingInput();

        var act = () => InputVerb.Tools(input).Scroll(direction, shift_wheel: true);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("shift_wheel").And.ContainEquivalentOf("left");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Without_shift_wheel_a_horizontal_scroll_uses_the_horizontal_wheel()
    {
        var input = ScrollingInput();

        await InputVerb.Tools(input).Scroll("right", x: 1, y: 2);

        input.Verify(s => s.ScrollAsync(1, 2, "right", 3, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- the advertised surface ---------------------------------------------------------------

    [Fact]
    public void Scroll_takes_the_direction_first_and_everything_else_is_optional()
    {
        // The positional break: it was scroll(x, y, direction, amount). Named arguments over MCP
        // are unaffected, but the schema's required list changes and CHANGELOG must say so.
        var parameters = typeof(InputTools).GetMethod(nameof(InputTools.Scroll))!.GetParameters();

        parameters.Select(p => p.Name).Should().Equal(
            "direction", "amount", "x", "y", "element_id", "shift_wheel");
        parameters[0].HasDefaultValue.Should().BeFalse("the direction is the one required argument");
        parameters.Skip(1).Should().OnlyContain(p => p.HasDefaultValue);
    }

    [Fact]
    public void Scroll_describes_the_optional_coordinates_the_element_id_and_shift_wheel()
    {
        var description = typeof(InputTools).GetMethod(nameof(InputTools.Scroll))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_id")
            .And.ContainEquivalentOf("optional", "the model has to know it may omit the coordinates")
            .And.ContainEquivalentOf("cursor", "and what happens when it does")
            .And.Contain("shift_wheel");
    }
}

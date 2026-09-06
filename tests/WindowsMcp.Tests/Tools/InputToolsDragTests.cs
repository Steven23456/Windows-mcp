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
/// B-2 (R7): <c>drag</c> with a duration, interpolated motion and an optional origin. The tool's
/// job is to resolve the two ends (roadmap C1/C2) and bound duration and steps; the path itself is
/// <c>DragPathTests</c> and the injection is the desktop test.
/// </summary>
[Trait("Category", "Unit")]
public class InputToolsDragTests
{
    private static Mock<IInputService> DraggingInput(int cursorX = 50, int cursorY = 60)
    {
        var input = new Mock<IInputService>();
        input.Setup(s => s.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(cursorX, cursorY));
        input.Setup(s => s.DragAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int fx, int fy, int tx, int ty, MouseButton b, int _, int __, CancellationToken ___)
                => new DragResult(fx, fy, tx, ty, b));
        return input;
    }

    // ---- the two ends -------------------------------------------------------------------------

    [Fact]
    public async Task Drag_between_two_points_uses_the_duration_and_steps_overload()
    {
        // The old press-jump-release overload stays for byte-compatibility, but `drag` itself must
        // now produce the intermediate moves a drop target needs to see.
        var input = DraggingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input)
            .Drag(from_x: 10, from_y: 20, to_x: 300, to_y: 400));

        input.Verify(s => s.DragAsync(10, 20, 300, 400, MouseButton.Left, 300, 20, It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.DragAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<MouseButton>(), It.IsAny<CancellationToken>()), Times.Never);
        InputVerb.Num(json, "fromX").Should().Be(10);
        InputVerb.Num(json, "fromY").Should().Be(20);
        InputVerb.Num(json, "toX").Should().Be(300);
        InputVerb.Num(json, "toY").Should().Be(400);
        InputVerb.Str(json, "button").Should().Be("left");
        InputVerb.Num(json, "durationMs").Should().Be(300, "300 ms is the documented default");
        InputVerb.Num(json, "steps").Should().Be(20, "20 steps is the documented default");
        InputVerb.Str(json, "fromTarget").Should().Be("point");
        InputVerb.Absent(json, "elementId").Should().BeTrue();
    }

    [Fact]
    public async Task Drag_with_no_origin_starts_at_the_live_cursor()
    {
        var input = DraggingInput(cursorX: 50, cursorY: 60);

        var json = InputVerb.Json(await InputVerb.Tools(input).Drag(to_x: 300, to_y: 400));

        input.Verify(s => s.GetCursorPositionAsync(It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.DragAsync(50, 60, 300, 400, MouseButton.Left, 300, 20, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "fromTarget").Should().Be("cursor");
        InputVerb.Num(json, "fromX").Should().Be(50);
        InputVerb.Num(json, "fromY").Should().Be(60);
    }

    [Fact]
    public async Task Drag_to_an_element_lands_on_its_centre()
    {
        var input = DraggingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(name: "Trash"));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia)
            .Drag(from_x: 10, from_y: 20, element_id: "el_12"));

        input.Verify(s => s.DragAsync(10, 20, 120, 210, MouseButton.Left, 300, 20, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "elementId").Should().Be("el_12");
        InputVerb.Str(json, "name").Should().Be("Trash");
        InputVerb.Num(json, "toX").Should().Be(120);
        InputVerb.Num(json, "toY").Should().Be(210);
    }

    [Fact]
    public async Task Drag_from_an_element_starts_at_its_centre()
    {
        var input = DraggingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_3", name: "report.txt", x: 0, y: 0, width: 20, height: 10));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia)
            .Drag(from_element_id: "el_3", to_x: 500, to_y: 500));

        input.Verify(s => s.DragAsync(10, 5, 500, 500, MouseButton.Left, 300, 20, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Str(json, "fromTarget").Should().Be("element");
        InputVerb.Num(json, "fromX").Should().Be(10);
        InputVerb.Num(json, "fromY").Should().Be(5);
    }

    [Fact]
    public async Task Drag_refuses_an_offscreen_destination()
    {
        var input = DraggingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_5", offscreen: true));

        var act = () => InputVerb.Tools(input, uia).Drag(from_x: 1, from_y: 2, element_id: "el_5");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_5").And.Contain("off-screen");
    }

    // ---- refusals ------------------------------------------------------------------------------

    [Fact]
    public async Task Drag_without_a_destination_is_refused()
    {
        // The origin has a sensible default (the cursor); the destination does not - there is no
        // "somewhere" to drop something.
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: 1, from_y: 2);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("to_x").And.Contain("element_id");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Drag_with_both_a_destination_point_and_a_destination_element_is_refused()
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(to_x: 1, to_y: 2, element_id: "el_12");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("element_id").And.Contain("coordinates");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Drag_with_both_an_origin_point_and_an_origin_element_is_refused()
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input)
            .Drag(from_x: 1, from_y: 2, to_x: 9, to_y: 9, from_element_id: "el_3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("from_element_id");
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(null, 1)]
    public async Task Drag_with_half_a_destination_pair_is_refused(int? toX, int? toY)
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: toX, to_y: toY);

        await act.Should().ThrowAsync<ArgumentException>();
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(null, 1)]
    public async Task Drag_with_half_an_origin_pair_is_refused(int? fromX, int? fromY)
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: fromX, from_y: fromY, to_x: 9, to_y: 9);

        await act.Should().ThrowAsync<ArgumentException>();
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(60000)]
    public async Task Drag_rejects_a_duration_outside_zero_to_ten_seconds(int durationMs)
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: 9, to_y: 9, duration_ms: durationMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("duration_ms").And.Contain("10000");
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public async Task Drag_accepts_the_ends_of_the_duration_range(int durationMs)
    {
        var input = DraggingInput();

        await InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: 9, to_y: 9, duration_ms: durationMs);

        input.Verify(s => s.DragAsync(0, 0, 9, 9, MouseButton.Left, durationMs, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(201)]
    public async Task Drag_rejects_a_step_count_outside_two_to_two_hundred(int steps)
    {
        // Fewer than two points is a jump (which is what B-2 is fixing); more than 200 is minutes
        // of SetCursorPos calls.
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: 9, to_y: 9, steps: steps);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("steps").And.Contain("200");
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(200)]
    public async Task Drag_accepts_the_ends_of_the_step_range(int steps)
    {
        var input = DraggingInput();

        await InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: 9, to_y: 9, steps: steps);

        input.Verify(s => s.DragAsync(0, 0, 9, 9, MouseButton.Left, 300, steps, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Drag_forwards_the_middle_button_so_the_service_can_refuse_it()
    {
        // The rejection belongs to the service (H.InputSimulator has no MiddleButtonDown/Up); the
        // tool must not swallow the request and must not silently degrade it to a left drag.
        var input = DraggingInput();

        await InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: 9, to_y: 9, button: "middle");

        input.Verify(s => s.DragAsync(0, 0, 9, 9, MouseButton.Middle, 300, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Drag_rejects_an_unknown_button()
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: 9, to_y: 9, button: "fourth");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("button");
        input.VerifyNoOtherCalls();
    }

    // ---- the advertised surface ---------------------------------------------------------------

    [Fact]
    public void Drag_keeps_the_four_coordinates_as_its_first_four_parameters()
    {
        var parameters = typeof(InputTools).GetMethod(nameof(InputTools.Drag))!.GetParameters();

        parameters.Select(p => p.Name).Should().Equal(
            "from_x", "from_y", "to_x", "to_y", "element_id", "from_element_id", "button", "duration_ms", "steps");
        parameters.Should().OnlyContain(p => p.HasDefaultValue);
    }

    [Fact]
    public void Drag_describes_the_element_targets_and_the_motion_controls()
    {
        var description = typeof(InputTools).GetMethod(nameof(InputTools.Drag))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_id")
            .And.Contain("duration_ms")
            .And.Contain("steps")
            .And.ContainEquivalentOf("cursor", "an omitted origin starts at the pointer - that must be discoverable");
    }

    // ---- the refusals name the END of the drag they are about (roadmap C1) --------------------

    [Theory]
    [InlineData(1, null)]
    [InlineData(null, 1)]
    public async Task Drag_naming_half_a_destination_is_refused_by_the_DESTINATION_parameter_names(int? toX, int? toY)
    {
        // Both ends go through one resolver, so the only thing that keeps the message actionable is
        // the parameter names handed to it. "x and y must be given together" would send the caller
        // looking for parameters `drag` does not have.
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: toX, to_y: toY);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("to_x").And.Contain("to_y");
        message.Should().NotContain("from_", "this refusal is about the destination, and the origin was given in full");
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(null, 1)]
    public async Task Drag_naming_half_an_origin_is_refused_by_the_ORIGIN_parameter_names(int? fromX, int? fromY)
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: fromX, from_y: fromY, to_x: 9, to_y: 9);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("from_x").And.Contain("from_y");
    }

    [Fact]
    public async Task Drag_with_both_a_destination_point_and_element_names_only_the_destination_parameters()
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input).Drag(from_x: 0, from_y: 0, to_x: 1, to_y: 2, element_id: "el_12");

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain("to_x").And.Contain("to_y").And.Contain("element_id");
        message.Should().NotContain("from_", "the origin is not what the caller over-specified");
    }

    [Fact]
    public async Task Drag_with_both_an_origin_point_and_element_names_only_the_origin_parameters()
    {
        var input = DraggingInput();

        var act = () => InputVerb.Tools(input)
            .Drag(from_x: 1, from_y: 2, to_x: 9, to_y: 9, from_element_id: "el_3");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("from_x").And.Contain("from_y").And.Contain("from_element_id");
    }
}

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
/// B-1 (R5): the <c>type</c> TOOL — target resolution (roadmap C1), the caret parsing, and the
/// options it hands to <c>IInputService.TypeAsync</c>. What the service then DOES with those
/// options is <c>TypePlannerTests</c> and <c>InputServiceTypeTests</c>; nothing here injects.
/// </summary>
[Trait("Category", "Unit")]
public class InputToolsTypeTests
{
    private static Mock<IInputService> TypingInput(TypeResult? result = null, List<string>? log = null)
    {
        var input = new Mock<IInputService>();
        input.Setup(s => s.TypeAsync(It.IsAny<string>(), It.IsAny<TypeOptions>(), It.IsAny<CancellationToken>()))
            .Callback((string text, TypeOptions _, CancellationToken __) => log?.Add($"type:{text}"))
            .ReturnsAsync((string text, TypeOptions _, CancellationToken __) => result ?? new TypeResult(text.Length));
        input.Setup(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((int x, int y, MouseButton _, int __, CancellationToken ___) => log?.Add($"click:{x},{y}"))
            .ReturnsAsync((int x, int y, MouseButton b, int c, CancellationToken _) => new ClickResult(x, y, b, c));
        return input;
    }

    // ---- no target: today's behaviour, unchanged ----------------------------------------------

    [Fact]
    public async Task Type_with_no_target_types_at_the_focus_and_clicks_nothing()
    {
        var input = TypingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Type("hello"));

        input.Verify(s => s.TypeAsync("hello", new TypeOptions(false, CaretPosition.Idle, false, 5), It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "`type(text)` has always typed wherever focus already is; B-1 must not start moving the pointer");
        InputVerb.Num(json, "typed").Should().Be(5);
        InputVerb.Str(json, "method").Should().Be("keys");
        InputVerb.Absent(json, "x").Should().BeTrue();
        InputVerb.Absent(json, "y").Should().BeTrue();
        InputVerb.Absent(json, "elementId").Should().BeTrue();
    }

    [Fact]
    public async Task Type_reports_the_method_and_the_clipboard_restore_the_service_reported()
    {
        // The response is the OUTCOME, not the request (A-7's rule): whether the text was pasted
        // and whether the user's clipboard came back are facts only the service knows.
        var input = TypingInput(new TypeResult(5000, "paste", true));

        var json = InputVerb.Json(await InputVerb.Tools(input).Type(new string('a', 5000)));

        InputVerb.Num(json, "typed").Should().Be(5000);
        InputVerb.Str(json, "method").Should().Be("paste");
        InputVerb.Flag(json, "clipboardRestored").Should().BeTrue();
    }

    [Fact]
    public async Task Type_omits_the_clipboard_flag_when_no_paste_happened()
    {
        var input = TypingInput(new TypeResult(2, "keys"));

        var json = InputVerb.Json(await InputVerb.Tools(input).Type("hi"));

        InputVerb.Absent(json, "clipboardRestored").Should().BeTrue(
            "a keys-mode call never touched the clipboard; reporting false would claim it tried and failed");
    }

    [Fact]
    public async Task Type_reports_a_clipboard_that_could_not_be_restored()
    {
        var input = TypingInput(new TypeResult(300, "paste", false));

        var json = InputVerb.Json(await InputVerb.Tools(input).Type(new string('a', 300)));

        InputVerb.Flag(json, "clipboardRestored").Should().BeFalse();
    }

    // ---- targets (roadmap C1) -----------------------------------------------------------------

    [Fact]
    public async Task Type_at_coordinates_clicks_there_first_and_then_types()
    {
        // Order matters: typing before the click would put the text in whatever had focus.
        var log = new List<string>();
        var input = TypingInput(log: log);

        var json = InputVerb.Json(await InputVerb.Tools(input).Type("hi", x: 10, y: 20));

        log.Should().Equal("click:10,20", "type:hi");
        input.Verify(s => s.ClickAsync(10, 20, MouseButton.Left, 1, It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Num(json, "x").Should().Be(10);
        InputVerb.Num(json, "y").Should().Be(20);
    }

    [Fact]
    public async Task Type_by_element_id_clicks_the_centre_first_and_reports_the_element()
    {
        var log = new List<string>();
        var input = TypingInput(log: log);
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_12", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(name: "Search box"));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia).Type("hi", element_id: "el_12"));

        log.Should().Equal("click:120,210", "type:hi");
        InputVerb.Str(json, "elementId").Should().Be("el_12");
        InputVerb.Str(json, "name").Should().Be("Search box");
        InputVerb.Num(json, "x").Should().Be(120);
        InputVerb.Num(json, "y").Should().Be(210);
    }

    [Fact]
    public async Task Type_refuses_an_offscreen_element_before_a_single_character_is_sent()
    {
        var input = TypingInput();
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("el_5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(InputVerb.Element(id: "el_5", offscreen: true));

        var act = () => InputVerb.Tools(input, uia).Type("secret", element_id: "el_5");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_5").And.Contain("off-screen");
        input.VerifyNoOtherCalls();   // text typed into an unknown window is a data leak, not a failed call
    }

    [Fact]
    public async Task Type_with_both_coordinates_and_an_element_id_is_refused()
    {
        var input = TypingInput();

        var act = () => InputVerb.Tools(input).Type("hi", x: 1, y: 2, element_id: "el_12");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("element_id").And.Contain("coordinates");
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(5, null)]
    [InlineData(null, 5)]
    public async Task Type_with_half_a_coordinate_pair_is_refused(int? x, int? y)
    {
        var input = TypingInput();

        var act = () => InputVerb.Tools(input).Type("hi", x: x, y: y);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("x").And.Contain("y");
        input.VerifyNoOtherCalls();
    }

    // ---- the options -------------------------------------------------------------------------

    [Fact]
    public async Task Type_forwards_clear_press_enter_and_pace_as_given()
    {
        var input = TypingInput();

        await InputVerb.Tools(input).Type("hi", clear: true, press_enter: true, pace_ms: 40);

        input.Verify(s => s.TypeAsync("hi", new TypeOptions(true, CaretPosition.Idle, true, 40), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("idle", CaretPosition.Idle)]
    [InlineData("IDLE", CaretPosition.Idle)]
    [InlineData("start", CaretPosition.Start)]
    [InlineData("Start", CaretPosition.Start)]
    [InlineData("end", CaretPosition.End)]
    [InlineData("END", CaretPosition.End)]
    public async Task Type_parses_the_caret_case_insensitively(string caret, CaretPosition expected)
    {
        var input = TypingInput();

        await InputVerb.Tools(input).Type("hi", caret: caret);

        input.Verify(s => s.TypeAsync("hi", new TypeOptions(false, expected, false, 5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("middle")]
    [InlineData("top")]
    [InlineData("")]
    public async Task Type_rejects_an_unknown_caret_and_lists_the_ones_it_takes(string caret)
    {
        var input = TypingInput();

        var act = () => InputVerb.Tools(input).Type("hi", caret: caret);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("caret").And.Contain("idle").And.Contain("start").And.Contain("end");
        input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    public async Task Type_rejects_a_negative_pace_by_name(int paceMs)
    {
        var input = TypingInput();

        var act = () => InputVerb.Tools(input).Type("hi", pace_ms: paceMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("pace_ms");
        input.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Type_accepts_a_zero_pace()
    {
        var input = TypingInput();

        await InputVerb.Tools(input).Type("hi", pace_ms: 0);

        input.Verify(s => s.TypeAsync("hi", new TypeOptions(false, CaretPosition.Idle, false, 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Type_of_an_empty_string_still_reaches_the_service()
    {
        // `type("", clear:true)` is how a field is emptied; refusing empty text would remove that.
        var input = TypingInput();

        var json = InputVerb.Json(await InputVerb.Tools(input).Type("", clear: true));

        input.Verify(s => s.TypeAsync("", It.IsAny<TypeOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Num(json, "typed").Should().Be(0);
    }

    // ---- the advertised surface ---------------------------------------------------------------

    [Fact]
    public void Type_keeps_text_as_its_first_parameter_and_adds_the_rest_after_it()
    {
        var parameters = typeof(InputTools).GetMethod(nameof(InputTools.Type))!.GetParameters();

        parameters.Select(p => p.Name).Should().Equal(
            "text", "x", "y", "element_id", "clear", "caret", "press_enter", "pace_ms");
        parameters[0].HasDefaultValue.Should().BeFalse("the text is the one required argument");
        parameters.Skip(1).Should().OnlyContain(p => p.HasDefaultValue, "`type(text)` must keep working");
    }

    [Fact]
    public void Type_describes_the_target_the_editing_options_and_the_paste_path()
    {
        var description = typeof(InputTools).GetMethod(nameof(InputTools.Type))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_id")
            .And.Contain("clear")
            .And.Contain("caret")
            .And.Contain("press_enter")
            .And.ContainEquivalentOf("paste", "the model should know long text goes via the clipboard");
    }
}

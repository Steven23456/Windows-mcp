using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// B-7 (R85-R106): the two batch tools. Everything here is mocked — the interesting facts are the
/// ORDER of what would have been injected (Ctrl down FIRST, up LAST, up even when a click throws)
/// and the refuse-before-you-touch-anything rule: every target is resolved before the first
/// click, so a batch with a bad entry leaves the desktop exactly as it found it. The live
/// counterpart, including the stuck-Ctrl check, is <c>InputToolsBatchDesktopTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class InputToolsBatchTests
{
    /// <summary>Clicks, held keys and typing in ONE log: the interleaving is the contract.</summary>
    private static Mock<IInputService> Recording(List<string> log)
    {
        var input = new Mock<IInputService>();
        input.Setup(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((int x, int y, MouseButton b, int c, CancellationToken _) => log.Add($"click:{x},{y},{b},{c}"))
            .ReturnsAsync((int x, int y, MouseButton b, int c, CancellationToken _) => new ClickResult(x, y, b, c));
        input.Setup(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string k, CancellationToken _) => log.Add($"down:{k}"))
            .Returns(Task.CompletedTask);
        input.Setup(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string k, CancellationToken _) => log.Add($"up:{k}"))
            .Returns(Task.CompletedTask);
        input.Setup(s => s.TypeAsync(It.IsAny<string>(), It.IsAny<TypeOptions>(), It.IsAny<CancellationToken>()))
            .Callback((string text, TypeOptions o, CancellationToken _) =>
                log.Add($"type:{text},clear={o.Clear},enter={o.PressEnter},pace={o.PaceMs}"))
            .ReturnsAsync((string text, TypeOptions _, CancellationToken __) => new TypeResult(text.Length));
        return input;
    }

    private static Mock<IUIAutomationService> Elements(params ElementInfo[] elements)
    {
        var uia = new Mock<IUIAutomationService>();
        foreach (var element in elements)
            uia.Setup(s => s.GetElementAsync(element.ElementId, It.IsAny<CancellationToken>())).ReturnsAsync(element);
        return uia;
    }

    // ---- multi_select: the Ctrl bracket -------------------------------------------------------

    [Fact]
    public async Task Multi_select_holds_ctrl_down_first_clicks_each_point_and_releases_it_last()
    {
        var log = new List<string>();
        var input = Recording(log);

        var json = InputVerb.Json(await InputVerb.Tools(input)
            .MultiSelect("""[{"x":10,"y":20},{"x":30,"y":40}]"""));

        log.Should().Equal(
            "down:ctrl", "click:10,20,Left,1", "click:30,40,Left,1", "up:ctrl");
        InputVerb.Num(json, "count").Should().Be(2);
        InputVerb.Flag(json, "ctrl").Should().BeTrue();
        var results = json.GetProperty("results");
        results.GetArrayLength().Should().Be(2);
        results[0].GetProperty("index").GetInt32().Should().Be(0);
        results[0].GetProperty("x").GetInt32().Should().Be(10);
        results[0].GetProperty("y").GetInt32().Should().Be(20);
        results[0].GetProperty("ok").GetBoolean().Should().BeTrue();
        results[1].GetProperty("index").GetInt32().Should().Be(1);
        InputVerb.Absent(json, "failedIndex").Should().BeTrue("nothing failed");
        InputVerb.Absent(json, "error").Should().BeTrue();
    }

    [Fact]
    public async Task Multi_select_with_ctrl_false_clicks_without_the_modifier()
    {
        // Upstream's press_ctrl=False: a plain sequence of clicks, for a list that selects on
        // click or a target that treats Ctrl+click as something else entirely.
        var log = new List<string>();
        var input = Recording(log);

        var json = InputVerb.Json(await InputVerb.Tools(input)
            .MultiSelect("""[{"x":10,"y":20},{"x":30,"y":40}]""", ctrl: false));

        log.Should().Equal("click:10,20,Left,1", "click:30,40,Left,1");
        input.Verify(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        input.Verify(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        InputVerb.Flag(json, "ctrl").Should().BeFalse();
    }

    [Fact]
    public async Task Multi_select_resolves_element_ids_to_their_centres_and_reports_them()
    {
        var log = new List<string>();
        var input = Recording(log);
        var uia = Elements(
            InputVerb.Element("el_3", "First", x: 100, y: 200, width: 40, height: 20),
            InputVerb.Element("el_9", "Second", x: 300, y: 400, width: 20, height: 10));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia)
            .MultiSelect("""[{"element_id":"el_3"},{"element_id":"el_9"}]"""));

        log.Should().Equal("down:ctrl", "click:120,210,Left,1", "click:310,405,Left,1", "up:ctrl");
        var results = json.GetProperty("results");
        results[0].GetProperty("elementId").GetString().Should().Be("el_3");
        results[0].GetProperty("name").GetString().Should().Be("First");
        results[1].GetProperty("elementId").GetString().Should().Be("el_9");
        results[1].GetProperty("x").GetInt32().Should().Be(310);
    }

    [Fact]
    public async Task Multi_select_resolves_every_target_before_it_clicks_anything()
    {
        // THE rule of the batch tools: a refusal after three clicks has already changed the
        // desktop and cannot be undone. An off-screen element in entry 2 must cost entry 1 its
        // click too - nothing is injected at all.
        var log = new List<string>();
        var input = Recording(log);
        var uia = Elements(
            InputVerb.Element("el_3", "First"),
            InputVerb.Element("el_9", "Hidden", offscreen: true));

        Func<Task> act = () => InputVerb.Tools(input, uia)
            .MultiSelect("""[{"element_id":"el_3"},{"element_id":"el_9"}]""");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("el_9").And.ContainEquivalentOf("off-screen");
        log.Should().BeEmpty("not one click, and no Ctrl left held down");
    }

    [Fact]
    public async Task Multi_select_refuses_a_malformed_batch_before_any_input()
    {
        var log = new List<string>();
        var input = Recording(log);

        Func<Task> act = () => InputVerb.Tools(input).MultiSelect("""[{"x":1,"y":2},{}]""");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("targets_json");
        log.Should().BeEmpty();
    }

    [Fact]
    public async Task Multi_select_stops_at_the_first_failing_click_and_reports_how_far_it_got()
    {
        // A failure DURING the clicks is not an exception: the caller has to learn which entries
        // already landed, or it cannot recover the selection state.
        var log = new List<string>();
        var input = Recording(log);
        input.Setup(s => s.ClickAsync(30, 40, It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SetCursorPos(30,40) failed"));

        var json = InputVerb.Json(await InputVerb.Tools(input)
            .MultiSelect("""[{"x":10,"y":20},{"x":30,"y":40},{"x":50,"y":60}]"""));

        InputVerb.Num(json, "failedIndex").Should().Be(1);
        InputVerb.Str(json, "error").Should().Contain("SetCursorPos");
        json.GetProperty("results").GetArrayLength().Should().Be(1, "only entry 0 landed");
        json.GetProperty("results")[0].GetProperty("index").GetInt32().Should().Be(0);
        InputVerb.Num(json, "count").Should().Be(3, "count is the size of the batch that was asked for");
        log.Should().NotContain("click:50,60,Left,1", "the batch stops at the first failure");
        log.Should().NotBeEmpty();
        log[^1].Should().Be("up:ctrl", "Ctrl is released in a finally - a stuck modifier breaks the desktop");
        input.Verify(s => s.KeyUpAsync("ctrl", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Multi_select_releases_ctrl_when_the_very_first_click_throws()
    {
        var log = new List<string>();
        var input = Recording(log);
        input.Setup(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var json = InputVerb.Json(await InputVerb.Tools(input).MultiSelect("""[{"x":10,"y":20}]"""));

        InputVerb.Num(json, "failedIndex").Should().Be(0);
        json.GetProperty("results").GetArrayLength().Should().Be(0);
        log.Should().Equal("down:ctrl", "up:ctrl");
    }

    [Fact]
    public async Task Multi_select_refuses_an_empty_element_id_before_any_input()
    {
        // The parser accepts "" as a target shape (BatchTargetsTests pins that), so the element
        // lookup is what refuses it - and it happens in the resolve pass, before Ctrl goes down.
        var log = new List<string>();
        var input = Recording(log);
        var uia = new Mock<IUIAutomationService>();
        uia.Setup(s => s.GetElementAsync("", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Unknown element id ''"));

        Func<Task> act = () => InputVerb.Tools(input, uia).MultiSelect("""[{"x":1,"y":2},{"element_id":""}]""");

        await act.Should().ThrowAsync<KeyNotFoundException>();
        log.Should().BeEmpty("not one click, and no Ctrl left held down");
    }

    [Fact]
    public async Task Multi_select_holds_one_ctrl_across_a_hundred_clicks()
    {
        // The modifier is per BATCH, not per click: a hundred entries are still one Ctrl down and
        // one Ctrl up, or the target sees a hundred separate selections.
        var log = new List<string>();
        var input = Recording(log);
        var json = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => $"{{\"x\":{i},\"y\":{i}}}")) + "]";

        var result = InputVerb.Json(await InputVerb.Tools(input).MultiSelect(json));

        log.Should().HaveCount(102);
        log[0].Should().Be("down:ctrl");
        log[^1].Should().Be("up:ctrl");
        log.Count(entry => entry.StartsWith("click:", StringComparison.Ordinal)).Should().Be(100);
        input.Verify(s => s.KeyDownAsync("ctrl", It.IsAny<CancellationToken>()), Times.Once);
        input.Verify(s => s.KeyUpAsync("ctrl", It.IsAny<CancellationToken>()), Times.Once);
        InputVerb.Num(result, "count").Should().Be(100);
        result.GetProperty("results").GetArrayLength().Should().Be(100,
            "every entry that ran is reported, so the caller can tell a partial batch from a whole one");
        InputVerb.Absent(result, "failedIndex").Should().BeTrue();
    }

    // ---- multi_edit: click, then B-1's typing path, per entry ---------------------------------

    [Fact]
    public async Task Multi_edit_clicks_each_target_then_types_its_text_with_its_own_options()
    {
        var log = new List<string>();
        var input = Recording(log);
        var uia = Elements(InputVerb.Element("el_3", "Surname", x: 100, y: 200, width: 40, height: 20));

        var json = InputVerb.Json(await InputVerb.Tools(input, uia).MultiEdit(
            """[{"x":10,"y":20,"text":"alpha"},{"element_id":"el_3","text":"beta","clear":true,"press_enter":true}]"""));

        log.Should().Equal(
            "click:10,20,Left,1", "type:alpha,clear=False,enter=False,pace=5",
            "click:120,210,Left,1", "type:beta,clear=True,enter=True,pace=5");
        InputVerb.Num(json, "count").Should().Be(2);
        var results = json.GetProperty("results");
        results[0].GetProperty("index").GetInt32().Should().Be(0);
        results[0].GetProperty("typed").GetInt32().Should().Be(5);
        results[0].GetProperty("method").GetString().Should().Be("keys");
        results[0].GetProperty("ok").GetBoolean().Should().BeTrue();
        results[1].GetProperty("elementId").GetString().Should().Be("el_3");
        results[1].GetProperty("name").GetString().Should().Be("Surname");
        results[1].GetProperty("x").GetInt32().Should().Be(120);
    }

    [Fact]
    public async Task Multi_edit_never_holds_ctrl()
    {
        var log = new List<string>();
        var input = Recording(log);

        await InputVerb.Tools(input).MultiEdit("""[{"x":1,"y":2,"text":"a"}]""");

        input.Verify(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "multi_edit fills fields; a held Ctrl would turn every keystroke into a shortcut");
        log.Should().NotContain(entry => entry.StartsWith("down:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Multi_edit_resolves_every_entry_before_it_types_anything()
    {
        var log = new List<string>();
        var input = Recording(log);
        var uia = Elements(InputVerb.Element("el_3", "First"), InputVerb.Boundless("el_9"));

        Func<Task> act = () => InputVerb.Tools(input, uia)
            .MultiEdit("""[{"element_id":"el_3","text":"a"},{"element_id":"el_9","text":"b"}]""");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("el_9");
        log.Should().BeEmpty("half a form filled in is worse than none");
    }

    [Fact]
    public async Task Multi_edit_stops_at_the_first_failing_entry_and_reports_the_index()
    {
        var log = new List<string>();
        var input = Recording(log);
        input.Setup(s => s.TypeAsync("beta", It.IsAny<TypeOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the field went away"));

        var json = InputVerb.Json(await InputVerb.Tools(input).MultiEdit(
            """[{"x":1,"y":2,"text":"alpha"},{"x":3,"y":4,"text":"beta"},{"x":5,"y":6,"text":"gamma"}]"""));

        InputVerb.Num(json, "failedIndex").Should().Be(1);
        InputVerb.Str(json, "error").Should().Contain("the field went away");
        json.GetProperty("results").GetArrayLength().Should().Be(1);
        log.Should().NotContain("click:5,6,Left,1", "entry 2 is never touched");
    }

    [Fact]
    public async Task Multi_edit_refuses_an_entry_without_text_before_any_input()
    {
        var log = new List<string>();
        var input = Recording(log);

        Func<Task> act = () => InputVerb.Tools(input).MultiEdit("""[{"x":1,"y":2,"text":"a"},{"x":3,"y":4}]""");

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("entries_json");
        log.Should().BeEmpty();
    }

    // ---- what the model is told ---------------------------------------------------------------

    [Theory]
    [InlineData(nameof(InputTools.MultiSelect))]
    [InlineData(nameof(InputTools.MultiEdit))]
    public void Both_batch_tools_are_neither_read_only_nor_idempotent(string method)
    {
        // They inject clicks and keystrokes: running one twice selects twice and types twice.
        var attribute = typeof(InputTools).GetMethod(method)!.GetCustomAttribute<McpServerToolAttribute>();

        attribute.Should().NotBeNull();
        attribute!.ReadOnly.Should().NotBe(true);
        attribute.Idempotent.Should().NotBe(true);
    }

    [Fact]
    public void Multi_select_describes_the_target_shapes_the_ctrl_default_and_the_stop_rule()
    {
        var description = typeof(InputTools).GetMethod(nameof(InputTools.MultiSelect))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_id").And.Contain("x").And.Contain("y")
            .And.ContainEquivalentOf("ctrl")
            .And.ContainEquivalentOf("first failure", "the model has to know the batch is not atomic")
            .And.Contain("failedIndex");
    }

    [Fact]
    public void Multi_edit_describes_the_entry_shape_and_the_per_entry_options()
    {
        var description = typeof(InputTools).GetMethod(nameof(InputTools.MultiEdit))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should()
            .Contain("element_id").And.Contain("text")
            .And.Contain("clear").And.Contain("press_enter")
            .And.ContainEquivalentOf("first failure")
            .And.Contain("failedIndex");
    }

    [Fact]
    public void The_batch_parameters_keep_upstreams_names_and_the_ctrl_default()
    {
        var select = typeof(InputTools).GetMethod(nameof(InputTools.MultiSelect))!.GetParameters();
        var edit = typeof(InputTools).GetMethod(nameof(InputTools.MultiEdit))!.GetParameters();

        select.Select(p => p.Name).Should().Equal("targets_json", "ctrl");
        select[1].DefaultValue.Should().Be(true, "upstream's press_ctrl defaults on");
        edit.Select(p => p.Name).Should().Equal("entries_json");
        select[0].GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull();
        edit[0].GetCustomAttribute<DescriptionAttribute>().Should().NotBeNull();
    }
}

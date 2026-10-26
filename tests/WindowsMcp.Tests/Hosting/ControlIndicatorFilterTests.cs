using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using Xunit;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// S-11 (R5): the call-tool filter in <c>WindowsMcpHost.AddWindowsMcp</c>, through the REAL MCP
/// pipeline - the in-process HTTP host and the SDK's own client (<see cref="HttpTransportTests"/>'s
/// harness). A unit test of a lambda would stay green if the filter were registered after the
/// caller-facing error filter's catch, or read a captured indicator instead of the container's;
/// only the wire shows what a client receives.
/// <para>
/// <see cref="IControlIndicator"/> and every service a called tool touches are mocks swapped in
/// through <c>configureServices</c>, so nothing here clicks, types, focuses a window or draws -
/// Integration, not UIAutomation. The real indicator is <see cref="Services.ControlIndicatorTests"/>.
/// </para>
/// <para>
/// The takeover note is asserted by content, not by exact wording: it is the LAST block of the
/// result, it names the combination and says the user "took over", and two takeovers at different
/// times produce different notes (the note says when, in whatever format the implementation picks).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class ControlIndicatorFilterTests
{
    private const string Hotkey = "Ctrl+Alt+Shift+F13";
    private static readonly TakeoverRecord Takeover = new(new DateTimeOffset(2026, 10, 21, 14, 3, 7, TimeSpan.Zero), Hotkey);

    /// <summary>The host, the client, and the mocks; a takeover "happens" by putting a record in <see cref="Pend"/>.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public Mock<IControlIndicator> Indicator { get; } = new();
        public Mock<IInputService> Input { get; } = new();
        public Mock<IUIAutomationService> Uia { get; } = new();
        public Mock<IWindowService> Window { get; } = new();
        public Mock<IClipboardService> Clipboard { get; } = new();
        public Mock<IPowerShellService> PowerShell { get; } = new();
        public Mock<IScreenshotService> Screenshot { get; } = new();
        public Mock<IFlashOverlay> Flash { get; } = new();

        /// <summary>Everything the indicator and the input service saw, in order.</summary>
        public ConcurrentQueue<string> Events { get; } = new();

        private TakeoverRecord? _pending;
        private HttpTransportTests.Harness? _server;
        public McpClient Client { get; private set; } = null!;

        /// <summary>A takeover the indicator will hand to the next <c>ConsumeTakeover</c>, once - like the real one.</summary>
        public void Pend(TakeoverRecord record) => Volatile.Write(ref _pending, record);

        public void ResetCalls()
        {
            Indicator.Invocations.Clear();
            Events.Clear();
        }

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig.Indicator.Setup(i => i.BeginAction()).Callback(() => rig.Events.Enqueue("begin"));
            rig.Indicator.Setup(i => i.EndAction()).Callback(() => rig.Events.Enqueue("end"));
            rig.Indicator.Setup(i => i.ConsumeTakeover()).Returns(() => Interlocked.Exchange(ref rig._pending, null));
            rig.Indicator.Setup(i => i.SuspendForCapture()).Returns(() => Mock.Of<IDisposable>());

            rig._server = await HttpTransportTests.Harness.StartAsync(configureServices: services =>
            {
                services.AddSingleton(rig.Indicator.Object);
                services.AddSingleton(rig.Input.Object);
                services.AddSingleton(rig.Uia.Object);
                services.AddSingleton(rig.Window.Object);
                services.AddSingleton(rig.Clipboard.Object);
                services.AddSingleton(rig.PowerShell.Object);
                services.AddSingleton(rig.Screenshot.Object);
                services.AddSingleton(rig.Flash.Object);
            });
            rig.Client = await HttpTransportTests.ConnectAsync(rig._server.McpEndpoint);
            return rig;
        }

        public async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?>? args = null) =>
            await Client.CallToolAsync(tool, args ?? new Dictionary<string, object?>());

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            if (_server is not null) await _server.DisposeAsync();
        }
    }

    private static readonly Dictionary<string, object?> ClickArgs = new() { ["x"] = 10, ["y"] = 20 };
    private static readonly Dictionary<string, object?> WaitArgs = new() { ["seconds"] = 0.01 };

    private static bool IsNote(ContentBlock block) =>
        block is TextContentBlock t && t.Text.Contains("took over", StringComparison.OrdinalIgnoreCase);

    private static int NotesIn(CallToolResult result) => result.Content.Count(IsNote);

    private static string TextOf(ContentBlock block) => block.Should().BeOfType<TextContentBlock>().Which.Text;

    /// <summary>The note is the last block, it is the only note, and it says who took over with what.</summary>
    private static string ShouldEndWithTheNote(CallToolResult result)
    {
        result.Content.Should().NotBeEmpty();
        NotesIn(result).Should().Be(1, "the note is delivered once, as one block");
        var note = TextOf(result.Content[result.Content.Count - 1]);
        note.Should().ContainEquivalentOf("took over", "the AI is told the user took the controls back");
        note.Should().Contain(Hotkey, "and with which combination");
        return note;
    }

    // ---- Begin/End around driving tools ------------------------------------------------------------

    [Fact]
    public async Task A_driving_tool_begins_before_it_runs_and_ends_after_exactly_once()
    {
        await using var rig = await Rig.StartAsync();
        rig.Input.Setup(i => i.ClickAsync(10, 20, MouseButton.Left, 1, It.IsAny<CancellationToken>()))
            .Callback(() => rig.Events.Enqueue("click"))
            .ReturnsAsync(new ClickResult(10, 20, MouseButton.Left, 1));

        var result = await rig.CallAsync("click", ClickArgs);

        result.IsError.Should().NotBe(true);
        rig.Events.Should().Equal(["begin", "click", "end"],
            "the indicator is up before the first injected input and the call is over before it is released");
        rig.Indicator.Verify(i => i.BeginAction(), Times.Once);
        rig.Indicator.Verify(i => i.EndAction(), Times.Once);
        result.Content.Should().ContainSingle("with no takeover pending the result gains no block");
    }

    [Fact]
    public async Task Every_driving_tool_is_bracketed_by_exactly_one_begin_and_one_end()
    {
        // All thirteen, by their wire names, through the real pipeline: a filter that hard-coded the
        // input tools, or matched method names instead of wire names, misses some of these. The
        // services are loose mocks, so some calls end in an error result - the bracket must hold
        // either way.
        var args = new Dictionary<string, Dictionary<string, object?>>
        {
            ["click"] = new() { ["x"] = 10, ["y"] = 20 },
            ["drag"] = new() { ["from_x"] = 0, ["from_y"] = 0, ["to_x"] = 10, ["to_y"] = 20, ["duration_ms"] = 0 },
            ["hover"] = new() { ["x"] = 10, ["y"] = 20 },
            ["type"] = new() { ["text"] = "hi" },
            ["key"] = new() { ["key"] = "a" },
            ["shortcut"] = new() { ["shortcut"] = "ctrl+c" },
            ["scroll"] = new() { ["direction"] = "down", ["x"] = 10, ["y"] = 20 },
            ["multi_select"] = new() { ["targets_json"] = "[{\"x\":1,\"y\":2}]" },
            ["multi_edit"] = new() { ["entries_json"] = "[{\"x\":1,\"y\":2,\"text\":\"a\"}]" },
            ["interact_element"] = new() { ["element_id"] = "el_1", ["action"] = "click" },
            ["file_dialog"] = new() { ["path"] = "note.txt" },
            ["focus"] = new() { ["title"] = "no such window" },
            ["switch_to_window"] = new() { ["title"] = "no such window" },
        };
        args.Keys.Should().BeEquivalentTo(DrivingToolsTests.DesignNoteNames, "the sweep covers the design note's whole list");
        await using var rig = await Rig.StartAsync();

        var wrong = new List<string>();
        foreach (var (tool, toolArgs) in args)
        {
            rig.ResetCalls();
            try
            {
                await rig.CallAsync(tool, toolArgs);
            }
            catch (McpException ex)
            {
                wrong.Add($"{tool}: the server refused the call itself ({ex.Message})");
                continue;
            }

            var events = rig.Events.ToArray();
            if (!events.SequenceEqual(["begin", "end"]))
                wrong.Add($"{tool}: indicator saw [{string.Join(", ", events)}]");
        }

        string.Join(" | ", wrong).Should().BeEmpty("every driving tool shows the indicator for exactly the length of the call");
    }

    [Fact]
    public async Task Tools_that_inject_nothing_never_touch_the_indicator()
    {
        // window, launch and powershell are named in the design note; wait and clipboard live in
        // InputTools beside the driving tools, so a filter that keyed on the class would catch them.
        var args = new Dictionary<string, Dictionary<string, object?>>
        {
            ["wait"] = new() { ["seconds"] = 0.01 },
            ["clipboard"] = new() { ["action"] = "get" },
            ["window"] = new() { ["action"] = "list" },
            ["launch"] = new() { ["app_name"] = "nothing-real", ["wait_for_window"] = false },
            ["powershell"] = new() { ["command"] = "Get-Date" },
            ["multi_monitor"] = new(),
        };
        await using var rig = await Rig.StartAsync();

        var wrong = new List<string>();
        foreach (var (tool, toolArgs) in args)
        {
            rig.ResetCalls();
            await rig.CallAsync(tool, toolArgs);
            var events = rig.Events.ToArray();
            if (events.Length > 0)
                wrong.Add($"{tool}: indicator saw [{string.Join(", ", events)}]");
        }

        string.Join(" | ", wrong).Should().BeEmpty("only the thirteen driving tools show the indicator");
    }

    [Fact]
    public async Task The_bracket_holds_when_the_tool_refuses_its_arguments()
    {
        await using var rig = await Rig.StartAsync();

        var result = await rig.CallAsync("click", new() { ["x"] = 10, ["y"] = 20, ["button"] = "bogus" });

        result.IsError.Should().BeTrue();
        rig.Events.Should().Equal(["begin", "end"], "a refused driving call still ends what it began");
        rig.Indicator.Verify(i => i.EndAction(), Times.Once);
    }

    [Fact]
    public async Task The_bracket_holds_when_the_service_throws_a_caller_facing_error()
    {
        await using var rig = await Rig.StartAsync();
        rig.Input.Setup(i => i.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("refused for the test"));

        var result = await rig.CallAsync("click", ClickArgs);

        result.IsError.Should().BeTrue();
        TextOf(result.Content[0]).Should().Contain("refused for the test", "the caller-facing message still arrives verbatim");
        rig.Events.Should().Equal(["begin", "end"]);
        rig.Indicator.Verify(i => i.BeginAction(), Times.Once);
        rig.Indicator.Verify(i => i.EndAction(), Times.Once);
    }

    [Fact]
    public async Task The_bracket_holds_when_the_service_throws_an_unexpected_error()
    {
        // A bare Exception is not caller-facing: it passes through the error filter to the SDK's
        // masking. The End must not depend on which catch handled it.
        await using var rig = await Rig.StartAsync();
        rig.Input.Setup(i => i.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));

        var result = await rig.CallAsync("click", ClickArgs);

        result.IsError.Should().BeTrue();
        rig.Events.Should().Equal(["begin", "end"], "an unexpected fault must not leave the indicator up for ever");
        rig.Indicator.Verify(i => i.BeginAction(), Times.Once);
        rig.Indicator.Verify(i => i.EndAction(), Times.Once);
    }

    // ---- the takeover note -------------------------------------------------------------------------

    [Fact]
    public async Task A_pending_takeover_is_appended_as_the_last_block_of_the_next_result_of_a_non_driving_tool()
    {
        await using var rig = await Rig.StartAsync();
        rig.Pend(Takeover);

        var result = await rig.CallAsync("wait", WaitArgs);

        result.IsError.Should().NotBe(true, "the note is information; it does not turn a success into an error");
        result.Content.Should().HaveCount(2, "the tool's own result, then the note");
        TextOf(result.Content[0]).Should().Contain("waited", "the tool's own answer stays first and unchanged");
        ShouldEndWithTheNote(result);
        rig.Indicator.Verify(i => i.BeginAction(), Times.Never, "consuming the note does not make wait a driving tool");
    }

    [Fact]
    public async Task The_note_is_delivered_once()
    {
        await using var rig = await Rig.StartAsync();
        rig.Pend(Takeover);

        var first = await rig.CallAsync("wait", WaitArgs);
        var second = await rig.CallAsync("wait", WaitArgs);
        var third = await rig.CallAsync("click", ClickArgs);

        NotesIn(first).Should().Be(1);
        NotesIn(second).Should().Be(0, "the next result after that carries nothing");
        second.Content.Should().ContainSingle();
        NotesIn(third).Should().Be(0);
    }

    [Fact]
    public async Task The_note_is_appended_to_a_driving_tools_result_too()
    {
        await using var rig = await Rig.StartAsync();
        rig.Pend(Takeover);

        var result = await rig.CallAsync("click", ClickArgs);

        result.IsError.Should().NotBe(true);
        result.Content.Should().HaveCount(2);
        TextOf(result.Content[0]).Should().Contain("\"action\":\"click\"");
        ShouldEndWithTheNote(result);
        rig.Events.Should().Equal(["begin", "end"]);
    }

    [Fact]
    public async Task The_note_is_appended_after_a_caller_facing_error()
    {
        await using var rig = await Rig.StartAsync();
        rig.Pend(Takeover);

        var result = await rig.CallAsync("click", new() { ["x"] = 10, ["y"] = 20, ["button"] = "bogus" });

        result.IsError.Should().BeTrue("appending the note must not clear IsError");
        result.Content.Should().HaveCount(2, "the refusal, then the note");
        TextOf(result.Content[0]).Should().Contain("bogus", "the refusal stays first and intact - it is the answer");
        ShouldEndWithTheNote(result);
    }

    [Fact]
    public async Task The_note_is_never_lost_to_an_unexpected_error()
    {
        // The SDK masks a bare Exception after the filters have run. Either the filter appends the
        // note to that error result, or it leaves the takeover pending for the next result - but
        // the AI must hear about it exactly once.
        await using var rig = await Rig.StartAsync();
        rig.Input.Setup(i => i.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("boom"));
        rig.Pend(Takeover);

        var failed = await rig.CallAsync("click", ClickArgs);
        var next = await rig.CallAsync("wait", WaitArgs);
        var after = await rig.CallAsync("wait", WaitArgs);

        failed.IsError.Should().BeTrue();
        (NotesIn(failed) + NotesIn(next)).Should().Be(1, "delivered exactly once, on the failed result or the next one");
        if (NotesIn(failed) == 1) ShouldEndWithTheNote(failed);
        else ShouldEndWithTheNote(next);
        NotesIn(after).Should().Be(0);
    }

    [Fact]
    public async Task A_takeover_during_a_call_is_reported_in_that_calls_result()
    {
        // The note goes to the NEXT result produced after the takeover. A call in flight when the
        // user pressed the combination finishes after it, so its result is that next result - which
        // means the filter consults the indicator after the tool has run, not before.
        await using var rig = await Rig.StartAsync();
        rig.Input.Setup(i => i.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => rig.Pend(Takeover))
            .ReturnsAsync(new ClickResult(10, 20, MouseButton.Left, 1));

        var result = await rig.CallAsync("click", ClickArgs);

        ShouldEndWithTheNote(result);
    }

    [Fact]
    public async Task The_note_says_when_the_user_took_over()
    {
        await using var rig = await Rig.StartAsync();

        rig.Pend(Takeover);
        var first = ShouldEndWithTheNote(await rig.CallAsync("wait", WaitArgs));
        rig.Pend(Takeover with { At = Takeover.At.AddHours(3).AddMinutes(17).AddSeconds(41) });
        var second = ShouldEndWithTheNote(await rig.CallAsync("wait", WaitArgs));

        second.Should().NotBe(first, "the only difference between the two takeovers is WHEN, so the note must carry the time");
    }

    [Fact]
    public async Task The_note_is_the_last_block_after_an_image()
    {
        // screenshot returns its own CallToolResult (metadata text + image): the note goes after the
        // image, not into the metadata and not before the picture.
        await using var rig = await Rig.StartAsync();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        rig.Screenshot
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 1920, 1080, ImageFormat.Png, 1920, 1080, 1.0));
        rig.Window.Setup(w => w.EnumerateMonitorsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MonitorInfo(0, "Monitor0", 0, 0, 1920, 1080, true)]);
        rig.Input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CursorPosition(100, 100));
        rig.Pend(Takeover);

        var result = await rig.CallAsync("screenshot", new() { ["format"] = "png" });

        result.IsError.Should().NotBe(true);
        result.Content.Should().HaveCountGreaterThanOrEqualTo(3, "metadata, image, then the note");
        result.Content[result.Content.Count - 2].Should().BeOfType<ImageContentBlock>("the picture is untouched and directly before the note");
        ShouldEndWithTheNote(result);
        rig.Indicator.Verify(i => i.BeginAction(), Times.Never, "screenshot is not a driving tool");
    }

    // ---- F1: a takeover is never spent on a result the client will not read ------------------------

    private static readonly Dictionary<string, object?> SwitchArgs = new() { ["title"] = "Notepad" };

    private static ForegroundResult Brought() => new(
        new WindowInfo("Untitled - Notepad", 0x1234, 4242, "notepad", WindowState.Normal, new Bounds(0, 0, 800, 600), 0, true, false, 0),
        "substring", 100, false, "SetForegroundWindow", true);

    [Theory]
    [InlineData(false)]   // the late tool succeeds
    [InlineData(true)]    // the late tool fails with a caller-facing error
    public async Task A_takeover_is_not_spent_on_a_call_the_client_cancelled(bool lateRefusal)
    {
        // The client gives up at ~300 ms; the tool ignores its token and returns at ~1.5 s. That
        // result goes nowhere, so a note appended to it is lost for good. When the call's token is
        // cancelled by the time the tool returns, the filter leaves the takeover pending - on the
        // success branch and the caller-facing-error branch alike - and the NEXT result carries it.
        // switch_to_window, because it hands its token to the service, so the test can see that the
        // server-side call really was cancelled.
        await using var rig = await Rig.StartAsync();
        var cancelledOnReturn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Window.Setup(w => w.BringToFrontAsync(It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(async (string? _, long? _, CancellationToken ct) =>
            {
                await Task.Delay(1500, CancellationToken.None);   // a tool that ignores its token
                cancelledOnReturn.TrySetResult(ct.IsCancellationRequested);
                if (lateRefusal) throw new InvalidOperationException("refused, too late for anyone to read it");
                return Brought();
            });
        rig.Pend(Takeover);

        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var call = async () => await rig.Client.CallToolAsync("switch_to_window", SwitchArgs, cancellationToken: cts.Token);
            await call.Should().ThrowAsync<OperationCanceledException>("the client gave up at 300 ms");
        }

        (await cancelledOnReturn.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should()
            .BeTrue("precondition: the transport cancels the server-side call when the client gives up");
        (await Services.ControlIndicatorTests.WithinAsync(5000, () => rig.Events.Contains("end"))).Should()
            .BeTrue("the cancelled call ran to its end on the server");
        await Task.Delay(300);   // the filter's tail after EndAction - where the note is, or is not, taken

        var next = await rig.CallAsync("wait", WaitArgs);
        var after = await rig.CallAsync("wait", WaitArgs);

        ShouldEndWithTheNote(next);
        NotesIn(after).Should().Be(0, "delivered once");
    }

    [Fact]
    public async Task A_tool_that_throws_OperationCanceledException_leaves_the_takeover_for_the_next_result()
    {
        await using var rig = await Rig.StartAsync();
        rig.Input.Setup(i => i.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("the tool gave up"));
        rig.Pend(Takeover);

        CallToolResult? cancelled = null;
        using (var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            try
            {
                cancelled = await rig.Client.CallToolAsync("click", ClickArgs, cancellationToken: guard.Token);
            }
            catch (Exception ex) when (ex is McpException or OperationCanceledException)
            {
                // However the SDK reports a cancelled tool, it is not a result the note may ride on.
            }
        }
        var next = await rig.CallAsync("wait", WaitArgs);
        var after = await rig.CallAsync("wait", WaitArgs);

        if (cancelled is not null)
            NotesIn(cancelled).Should().Be(0, "a cancelled call's outcome is not where the note goes");
        ShouldEndWithTheNote(next);
        NotesIn(after).Should().Be(0, "delivered once");
    }

    // ---- F5: the indicator is a courtesy - its faults never reach the call -------------------------

    [Theory]
    [InlineData("BeginAction")]
    [InlineData("EndAction")]
    [InlineData("ConsumeTakeover")]
    public async Task A_faulting_indicator_never_fails_the_call_or_skips_the_tool(string member)
    {
        await using var rig = await Rig.StartAsync();
        rig.Input.Setup(i => i.ClickAsync(10, 20, MouseButton.Left, 1, It.IsAny<CancellationToken>()))
            .Callback(() => rig.Events.Enqueue("click"))
            .ReturnsAsync(new ClickResult(10, 20, MouseButton.Left, 1));
        var fault = new Exception($"injected: {member} faults");
        switch (member)
        {
            case "BeginAction":
                rig.Indicator.Setup(i => i.BeginAction()).Callback(() => rig.Events.Enqueue("begin")).Throws(fault);
                break;
            case "EndAction":
                rig.Indicator.Setup(i => i.EndAction()).Callback(() => rig.Events.Enqueue("end")).Throws(fault);
                break;
            default:
                rig.Indicator.Setup(i => i.ConsumeTakeover()).Throws(fault);
                break;
        }

        var result = await rig.CallAsync("click", ClickArgs);

        result.IsError.Should().NotBe(true, $"the indicator only informs: {member} faulting must not fail the click");
        TextOf(result.Content[0]).Should().Contain("\"action\":\"click\"", "the caller gets the tool's own result");
        rig.Events.Should().Equal(["begin", "click", "end"], "the tool still runs, once, and End still follows Begin");
        rig.Indicator.Verify(i => i.BeginAction(), Times.Once);
        rig.Indicator.Verify(i => i.EndAction(), Times.Once);
    }
}

using System.Runtime.InteropServices;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;
using WindowsMcp.Tools;
using Xunit;
using static WindowsMcp.Tests.Services.UiTree.SnapshotFixtures;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// E-5: <c>observe</c> on the seven action tools (click, type, key, shortcut, scroll, drag,
/// interact_element). After the action the tool waits <c>settle_ms</c> through
/// <c>Observation.Delay</c>, takes ONE snapshot (the foreground window, or the window the target
/// element belongs to) and appends its rendered text to the action's own result.
/// <para>
/// One ordered log across the input mock, the UIA mock and both delay seams, so every row can say
/// what happened in which order: the action, then the settle wait, then the snapshot - and for a
/// refusal, that nothing happened at all. Every tool runs through the same rows (Theory over the
/// seven) because the design is one shared helper, not seven copies: a tool that skipped the helper
/// fails here by name.
/// </para>
/// <para>
/// <c>observe:none</c> (the default) is pinned byte-for-byte against today's output, hard-coded
/// below: hundreds of existing tests and every client parse these strings.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class ObservationToolsTests
{
    /// <summary>The window el_12 (the default target) belongs to.</summary>
    private const long ElementWindow = 0x5A5A;

    /// <summary>The window el_3 (drag's origin element) belongs to.</summary>
    private const long OriginWindow = 0x3C3C;

    public static TheoryData<string> SevenTools => new()
    {
        "click", "type", "key", "shortcut", "scroll", "drag", "interact_element",
    };

    /// <summary>The tools whose default call in <see cref="Rig.Act"/> names an element.</summary>
    public static TheoryData<string> ElementTools => new()
    {
        "click", "type", "scroll", "drag", "interact_element",
    };

    /// <summary>
    /// What each tool returns TODAY for the call <see cref="Rig.Act"/> makes (element el_12, centre
    /// (120, 210)). <c>observe:none</c> must return exactly this, and an observation starts with it.
    /// </summary>
    private static readonly Dictionary<string, string> Baseline = new()
    {
        ["click"] = "{\"action\":\"click\",\"x\":120,\"y\":210,\"button\":\"left\",\"clicks\":1,\"elementId\":\"el_12\",\"name\":\"Save\",\"modifiers\":null}",
        ["type"] = "{\"typed\":2,\"method\":\"keys\",\"clipboardRestored\":null,\"x\":120,\"y\":210,\"elementId\":\"el_12\",\"name\":\"Save\"}",
        ["key"] = "pressed enter",
        ["shortcut"] = "pressed ctrl+s",
        ["scroll"] = "{\"direction\":\"down\",\"amount\":3,\"x\":120,\"y\":210,\"target\":\"element\",\"shiftWheel\":false,\"elementId\":\"el_12\",\"name\":\"Save\"}",
        ["drag"] = "{\"fromX\":1,\"fromY\":2,\"toX\":120,\"toY\":210,\"button\":\"left\",\"durationMs\":300,\"steps\":20,\"fromTarget\":\"point\",\"elementId\":\"el_12\",\"name\":\"Save\"}",
        ["interact_element"] = "{\"ElementId\":\"el_12\",\"Action\":\"click\",\"Method\":\"InvokePattern\",\"Detail\":null}",
    };

    /// <summary>The log of the action alone - what observe:none produces, and what every observation starts with.</summary>
    private static readonly Dictionary<string, string[]> ActionLog = new()
    {
        ["click"] = ["resolve:el_12", "click:120,210"],
        ["type"] = ["resolve:el_12", "click:120,210", "type:hi"],
        ["key"] = ["key:enter"],
        ["shortcut"] = ["shortcut:ctrl+s"],
        ["scroll"] = ["resolve:el_12", "scroll:120,210,down"],
        ["drag"] = ["resolve:el_12", "drag:1,2,120,210"],
        ["interact_element"] = ["interact:el_12:click"],
    };

    private static readonly string[] InputPrefixes =
        ["click:", "hover:", "type:", "key:", "shortcut:", "scroll:", "drag:", "down:", "up:", "interact:"];

    private static bool IsInput(string entry) => InputPrefixes.Any(p => entry.StartsWith(p, StringComparison.Ordinal));

    /// <summary>
    /// The mocks behind both tool classes, all writing to one <see cref="Log"/>. Failures are thrown
    /// synchronously from the mocked call, as the real services do from their argument checks.
    /// </summary>
    private sealed class Rig
    {
        public List<string> Log { get; } = [];
        public Mock<IInputService> Input { get; } = new();
        public Mock<IUIAutomationService> Uia { get; } = new();

        /// <summary>Thrown by the action call itself (click, type, key, shortcut, scroll, drag, interact).</summary>
        public Exception? ActionFailure { get; set; }

        /// <summary>Thrown by GetElementWindowAsync.</summary>
        public Exception? WindowFailure { get; set; }

        /// <summary>Thrown by the observation's settle wait.</summary>
        public Exception? SettleFailure { get; set; }

        /// <summary>What a snapshot of that scope throws; a scope not listed returns its result.</summary>
        public Dictionary<SnapshotScope, Exception> SnapshotFailure { get; } = new();

        public List<CancellationToken> SettleTokens { get; } = [];
        public List<CancellationToken> SnapshotTokens { get; } = [];
        public List<CancellationToken> WindowTokens { get; } = [];

        public SnapshotResult ForegroundSnap { get; set; } = Result(
            windows: [Window("Fg App - Notepad", hwnd: 0x1111, isActive: true)],
            active: Window("Fg App - Notepad", hwnd: 0x1111, isActive: true),
            interactive: [Element(id: "el_40", window: "Fg App - Notepad", controlType: "MenuItem", name: "File", action: "click")],
            elementCount: 9);

        public SnapshotResult WindowSnap { get; set; } = Result(
            windows: [Window("Save As", hwnd: ElementWindow, isActive: true)],
            active: Window("Save As", hwnd: ElementWindow, isActive: true),
            interactive: [Element(id: "el_41", window: "Save As", controlType: "Button", name: "OK", action: "click")],
            elementCount: 4);

        public InputTools Tools { get; }
        public UIAutomationTools UiaTools { get; }

        public Rig()
        {
            Input.Setup(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, MouseButton b, int c, CancellationToken _) =>
                {
                    Log.Add($"click:{x},{y}");
                    FailAction();
                    return Task.FromResult(new ClickResult(x, y, b, c));
                });
            Input.Setup(s => s.HoverAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, int _, CancellationToken _) => { Log.Add($"hover:{x},{y}"); return Task.CompletedTask; });
            Input.Setup(s => s.TypeAsync(It.IsAny<string>(), It.IsAny<TypeOptions>(), It.IsAny<CancellationToken>()))
                .Returns((string text, TypeOptions _, CancellationToken _) =>
                {
                    Log.Add($"type:{text}");
                    FailAction();
                    return Task.FromResult(new TypeResult(text.Length));
                });
            Input.Setup(s => s.PressKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken _) => { Log.Add($"key:{key}"); FailAction(); return Task.CompletedTask; });
            Input.Setup(s => s.PressShortcutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string chord, CancellationToken _) => { Log.Add($"shortcut:{chord}"); FailAction(); return Task.CompletedTask; });
            Input.Setup(s => s.ScrollAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, string direction, int _, bool _, CancellationToken _) =>
                {
                    Log.Add($"scroll:{x},{y},{direction}");
                    FailAction();
                    return Task.CompletedTask;
                });
            Input.Setup(s => s.DragAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int fx, int fy, int tx, int ty, MouseButton b, int _, int _, CancellationToken _) =>
                {
                    Log.Add($"drag:{fx},{fy},{tx},{ty}");
                    FailAction();
                    return Task.FromResult(new DragResult(fx, fy, tx, ty, b));
                });
            Input.Setup(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken _) => { Log.Add($"down:{key}"); return Task.CompletedTask; });
            Input.Setup(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken _) => { Log.Add($"up:{key}"); return Task.CompletedTask; });
            Input.Setup(s => s.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken _) => { Log.Add("cursor"); return Task.FromResult(new CursorPosition(500, 600)); });

            Uia.Setup(s => s.GetElementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) =>
                {
                    Log.Add($"resolve:{id}");
                    return Task.FromResult(id switch
                    {
                        "el_3" => InputVerb.Element(id: "el_3", name: "report.txt", x: 0, y: 0, width: 20, height: 10),
                        "el_off" => InputVerb.Element(id: "el_off", name: "Hidden row", offscreen: true),
                        _ => InputVerb.Element(id: id),
                    });
                });
            Uia.Setup(s => s.GetElementWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken ct) =>
                {
                    Log.Add($"window-of:{id}");
                    WindowTokens.Add(ct);
                    if (WindowFailure is { } e) throw e;
                    return Task.FromResult(id == "el_3" ? OriginWindow : ElementWindow);
                });
            Uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
                .Returns((SnapshotRequest r, CancellationToken ct) =>
                {
                    Log.Add($"snapshot:{r.Scope}:{r.WindowHwnd}");
                    SnapshotTokens.Add(ct);
                    if (SnapshotFailure.TryGetValue(r.Scope, out var e)) throw e;
                    return Task.FromResult(r.Scope == SnapshotScope.Window ? WindowSnap : ForegroundSnap);
                });
            Uia.Setup(s => s.InteractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((string id, string action, string? _, CancellationToken _) =>
                {
                    Log.Add($"interact:{id}:{action}");
                    FailAction();
                    return Task.FromResult(new InteractResult(id, action, "InvokePattern", null));
                });

            Tools = InputVerb.Tools(Input, Uia);
            // key(repeat)'s own pace seam: logged under another name so it can never pass for the settle wait.
            Tools.Delay = (span, _) => { Log.Add($"pace:{(int)span.TotalMilliseconds}"); return Task.CompletedTask; };
            Tools.Observation.Delay = Settle;
            UiaTools = new UIAutomationTools(Uia.Object);
            UiaTools.Observation.Delay = Settle;
        }

        private void FailAction()
        {
            if (ActionFailure is { } e) throw e;
        }

        private Task Settle(TimeSpan span, CancellationToken ct)
        {
            Log.Add($"settle:{(int)span.TotalMilliseconds}");
            SettleTokens.Add(ct);
            if (SettleFailure is { } e) throw e;
            return Task.CompletedTask;
        }

        /// <summary>The tool's standard call with observe and settle_ms given explicitly.</summary>
        public Task<string> Act(string tool, string observe, int settle = 250, CancellationToken ct = default) => tool switch
        {
            "click" => Tools.Click(element_id: "el_12", observe: observe, settle_ms: settle, ct: ct),
            "type" => Tools.Type("hi", element_id: "el_12", observe: observe, settle_ms: settle),
            "key" => Tools.Key("enter", observe: observe, settle_ms: settle, ct: ct),
            "shortcut" => Tools.Shortcut("ctrl+s", observe: observe, settle_ms: settle),
            "scroll" => Tools.Scroll("down", element_id: "el_12", observe: observe, settle_ms: settle),
            "drag" => Tools.Drag(from_x: 1, from_y: 2, element_id: "el_12", observe: observe, settle_ms: settle),
            "interact_element" => UiaTools.InteractElement("el_12", "click", null, observe: observe, settle_ms: settle),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "no such tool"),
        };

        /// <summary>The same call as every existing caller writes it: observe and settle_ms omitted.</summary>
        public Task<string> ActWithDefaults(string tool) => tool switch
        {
            "click" => Tools.Click(element_id: "el_12"),
            "type" => Tools.Type("hi", element_id: "el_12"),
            "key" => Tools.Key("enter"),
            "shortcut" => Tools.Shortcut("ctrl+s"),
            "scroll" => Tools.Scroll("down", element_id: "el_12"),
            "drag" => Tools.Drag(from_x: 1, from_y: 2, element_id: "el_12"),
            "interact_element" => UiaTools.InteractElement("el_12", "click", null),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "no such tool"),
        };

        public IEnumerable<string> InputSent => Log.Where(IsInput);

        public int Snapshots => Log.Count(e => e.StartsWith("snapshot:", StringComparison.Ordinal));

        /// <summary>No settle wait, no snapshot, no window lookup.</summary>
        public void NothingObserved(string because)
        {
            Log.Should().NotContain(e => e.StartsWith("settle:", StringComparison.Ordinal), because);
            Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never, because);
            Uia.Verify(s => s.GetElementWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, because);
        }
    }

    /// <summary>A note's claim that the action happened, so the caller does not send it again.</summary>
    private const string Performed =
        @"(?i)\b(was|were|has been|have been)\s+(performed|sent|done|carried out|completed|applied)\b|\bhappened\b|\btook effect\b";

    /// <summary>The header's claim about ids: it names ids...</summary>
    private const string NamesIds = @"(?i)\b(el_N|ids?)\b";

    /// <summary>...and says the earlier ones are no longer valid.</summary>
    private const string IdsNoLongerValid = @"(?i)no longer valid|replac|invalid|expired|evict";

    /// <summary>
    /// O5: <paramref name="text"/> is <paramref name="baseline"/>, a blank line, ONE header line and
    /// <paramref name="rendered"/>, LF only. Returns the header.
    /// </summary>
    private static string HeaderOf(string text, string baseline, string rendered)
    {
        text.Should().NotContain("\r", "the rendered snapshot uses LF and so does everything around it");
        text.Should().StartWith(baseline + "\n\n", "the action's result comes first, exactly as observe:none returns it, then a blank line");
        text.Should().EndWith(rendered, "the observation is SnapshotRenderer.Render of the snapshot the service returned");
        text.Length.Should().BeGreaterThan(baseline.Length + 2 + rendered.Length, "a header line sits between the blank line and the snapshot");
        var middle = text[(baseline.Length + 2)..^rendered.Length];
        middle.Should().EndWith("\n").And.NotStartWith("\n");
        var header = middle[..^1];
        header.Should().NotBeNullOrWhiteSpace().And.NotContain("\n", "exactly one header line");
        return header;
    }

    /// <summary>O6: the action's result, a blank line, and a note saying the observation failed and why.</summary>
    private static void ShouldBeActionPlusNote(string text, string baseline, string message)
    {
        text.Should().NotContain("\r");
        text.Should().StartWith(baseline + "\n\n", "the action's result is returned unchanged, then a blank line");
        var note = text[(baseline.Length + 2)..];
        note.Should().Contain(message, "the note carries the failure's own message");
        note.Should().MatchRegex("(?i)observ", "the note says it is the observation that failed");
        note.Should().MatchRegex("(?i)fail", "the note says the observation failed");
        note.Should().MatchRegex(Performed, "the note says the action WAS performed, so the caller does not repeat it");
        note.Should().NotContain("Cursor: (", "no snapshot was rendered");
        note.Should().NotContain("\n", "R6: the note is one line");
    }

    // ============================================================================================
    // O3: observe:none is today's tool, byte for byte
    // ============================================================================================

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task Observe_left_at_its_default_returns_todays_result_byte_for_byte_and_takes_no_snapshot(string tool)
    {
        var rig = new Rig();

        var text = await rig.ActWithDefaults(tool);

        text.Should().Be(Baseline[tool], "observe defaults to none, and none is the tool as it was before E-5");
        rig.Log.Should().Equal(ActionLog[tool], "the action and nothing else");
        rig.NothingObserved("observe:none takes no snapshot, waits for nothing and looks up no window");
    }

    public static TheoryData<string, string> SevenToolsTimesNone()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { "click", "type", "key", "shortcut", "scroll", "drag", "interact_element" })
            foreach (var none in new[] { "none", "None", "NONE" })
                data.Add(tool, none);
        return data;
    }

    [Theory]
    [MemberData(nameof(SevenToolsTimesNone))]
    public async Task Observe_none_in_any_case_is_the_default_exactly(string tool, string none)
    {
        var rig = new Rig();

        var text = await rig.Act(tool, none);

        text.Should().Be(Baseline[tool]);
        rig.Log.Should().Equal(ActionLog[tool]);
        rig.NothingObserved("none is none in any case");
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task Observe_none_never_waits_whatever_settle_ms_says(string tool)
    {
        var rig = new Rig();

        var text = await rig.Act(tool, "none", settle: 5000);

        text.Should().Be(Baseline[tool]);
        rig.NothingObserved("settle_ms is the wait before an observation, and there is none");
    }

    // ============================================================================================
    // O1: observe's vocabulary
    // ============================================================================================

    public static TheoryData<string, string> SevenToolsTimesBadObserve()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { "click", "type", "key", "shortcut", "scroll", "drag", "interact_element" })
            foreach (var bad in new[] { "", "   ", "desktop", "screen" })
                data.Add(tool, bad);
        return data;
    }

    [Theory]
    [MemberData(nameof(SevenToolsTimesBadObserve))]
    public async Task An_unknown_observe_is_refused_naming_the_three_values_before_the_target_is_resolved_or_any_input(string tool, string observe)
    {
        var rig = new Rig();

        Func<Task> act = () => rig.Act(tool, observe);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("observe");
        ex.Message.Should().Contain("none").And.Contain("foreground").And.Contain("window",
            "the refusal lists what observe takes");
        rig.Log.Should().BeEmpty("refused before the element is looked up and before any input (desktop is a snapshot scope, not an observe value)");
    }

    [Theory]
    [InlineData("Foreground", "snapshot:Foreground:0")]
    [InlineData("FOREGROUND", "snapshot:Foreground:0")]
    [InlineData("Window", "snapshot:Window:23130")]
    [InlineData("WINDOW", "snapshot:Window:23130")]
    public async Task Observe_values_are_case_insensitive(string observe, string snapshot)
    {
        var rig = new Rig();

        await rig.Act("click", observe);

        rig.Log.Should().ContainSingle(e => e.StartsWith("snapshot:", StringComparison.Ordinal)).Which.Should().Be(snapshot);
    }

    // ============================================================================================
    // O2: settle_ms 0-5000, whatever observe is
    // ============================================================================================

    public static TheoryData<string, int, string> SevenToolsTimesBadSettle()
    {
        var data = new TheoryData<string, int, string>();
        foreach (var tool in new[] { "click", "type", "key", "shortcut", "scroll", "drag", "interact_element" })
            foreach (var settle in new[] { -1, 5001 })
                foreach (var observe in new[] { "none", "foreground" })
                    data.Add(tool, settle, observe);
        return data;
    }

    [Theory]
    [MemberData(nameof(SevenToolsTimesBadSettle))]
    public async Task A_settle_ms_outside_0_to_5000_is_refused_stating_the_bounds_before_any_input(string tool, int settle, string observe)
    {
        var rig = new Rig();

        Func<Task> act = () => rig.Act(tool, observe, settle);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("settle_ms");
        ex.Message.Should().Contain("5000").And.MatchRegex(@"\b0\b", "the refusal states both bounds");
        rig.InputSent.Should().BeEmpty("refused before any input, whatever observe is");
        rig.NothingObserved("a refused call observes nothing");
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task Settle_ms_extremes_are_refused_too(int settle)
    {
        var rig = new Rig();

        Func<Task> act = () => rig.Act("click", "foreground", settle);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("settle_ms");
        rig.Log.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task Settle_ms_zero_skips_the_wait_entirely_and_still_observes(string tool)
    {
        var rig = new Rig();

        await rig.Act(tool, "foreground", settle: 0);

        rig.Log.Should().Equal(ActionLog[tool].Append("snapshot:Foreground:0"),
            "settle_ms 0 means no wait at all - not a zero-length one - and the snapshot still follows the action");
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task Settle_ms_5000_is_the_largest_accepted_and_is_waited_once(string tool)
    {
        var rig = new Rig();

        await rig.Act(tool, "foreground", settle: 5000);

        rig.Log.Should().Equal(ActionLog[tool].Concat(["settle:5000", "snapshot:Foreground:0"]));
    }

    // ============================================================================================
    // O4: action -> settle -> one foreground snapshot
    // ============================================================================================

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task Observe_foreground_acts_then_waits_settle_ms_then_takes_one_foreground_snapshot(string tool)
    {
        var rig = new Rig();

        await rig.Act(tool, "foreground");

        rig.Log.Should().Equal(ActionLog[tool].Concat(["settle:250", "snapshot:Foreground:0"]),
            "the action is performed exactly as with none, THEN the settle wait, THEN the snapshot");
        var expected = new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true);
        rig.Uia.Verify(s => s.SnapshotAsync(expected, It.IsAny<CancellationToken>()), Times.Once,
            "a foreground snapshot with its popups (R3: an opened menu is its own window): no title, no tree, "
            + "the server's element budget, no DOM, no handle");
        rig.Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        rig.Uia.Verify(s => s.GetElementWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "foreground needs no window lookup");
    }

    [Theory]
    [InlineData("click")]
    [InlineData("key")]
    public async Task The_settle_wait_and_the_snapshot_get_the_callers_token(string tool)
    {
        // click and key are the two of the seven that take a CancellationToken today.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Act(tool, "foreground", ct: cts.Token);

        rig.SettleTokens.Should().Equal([cts.Token], "a cancelled call must not sit out its settle wait");
        rig.SnapshotTokens.Should().Equal([cts.Token], "nor walk the desktop");
    }

    // ============================================================================================
    // O5: the text
    // ============================================================================================

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task Observe_foreground_returns_the_result_a_blank_line_a_header_and_the_rendered_snapshot(string tool)
    {
        var rig = new Rig();

        var text = await rig.Act(tool, "foreground");

        var header = HeaderOf(text, Baseline[tool], SnapshotRenderer.Render(rig.ForegroundSnap));
        header.Should().Contain("Observed").And.Contain("foreground").And.Contain("250 ms");
        header.Should().NotContainEquivalentOf("truncat", "R7: only a truncated observation says so");
        header.Should().MatchRegex(NamesIds).And.MatchRegex(IdsNoLongerValid,
            "the header warns that the earlier el_N ids are no longer valid: this was a real snapshot");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1234)]
    public async Task The_header_states_the_settle_time_that_was_used(int settle)
    {
        var rig = new Rig();

        var text = await rig.Act("click", "foreground", settle);

        HeaderOf(text, Baseline["click"], SnapshotRenderer.Render(rig.ForegroundSnap))
            .Should().Contain($"{settle} ms");
    }

    // ============================================================================================
    // O6: a failed observation is a note, never a failed action; a cancellation propagates
    // ============================================================================================

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task A_snapshot_that_throws_returns_the_action_result_and_a_note_instead_of_failing(string tool)
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] = new InvalidOperationException("UIA provider hung (wmcp-e5-probe)");

        var text = await rig.Act(tool, "foreground");

        ShouldBeActionPlusNote(text, Baseline[tool], "UIA provider hung (wmcp-e5-probe)");
        rig.Snapshots.Should().Be(1, "a failed observation is reported, not retried");
    }

    public static TheoryData<string> SnapshotFailureKinds => new()
    {
        "invalid-operation", "key-not-found", "timeout", "com", "plain", "argument",
    };

    private static Exception SnapshotFailureOf(string kind) => kind switch
    {
        "invalid-operation" => new InvalidOperationException("walk failed (wmcp-e5-kind)"),
        "key-not-found" => new KeyNotFoundException("walk failed (wmcp-e5-kind)"),
        "timeout" => new TimeoutException("walk failed (wmcp-e5-kind)"),
        "com" => new COMException("walk failed (wmcp-e5-kind)", unchecked((int)0x80004005)),
        "plain" => new Exception("walk failed (wmcp-e5-kind)"),
        "argument" => new ArgumentException("walk failed (wmcp-e5-kind)"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no such kind"),
    };

    /// <summary>
    /// R6: what the note says about the failure - the caller-facing message for a caller-facing
    /// exception, and for any other type its type name and NOT its message (which is what the SDK's
    /// masking keeps from clients everywhere else).
    /// </summary>
    private static string NoteFor(string kind) => kind switch
    {
        "com" => "COMException",
        "plain" => "Exception",
        _ => "walk failed (wmcp-e5-kind)",
    };

    [Theory]
    [MemberData(nameof(SnapshotFailureKinds))]
    public async Task Any_snapshot_failure_but_a_cancellation_becomes_the_note(string kind)
    {
        // Including the non-caller-facing ones: a masked "An error occurred invoking 'click'" would
        // tell the caller the click failed when it did not. Re-pinned by R6 for com and plain.
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] = SnapshotFailureOf(kind);

        var text = await rig.Act("click", "foreground");

        ShouldBeActionPlusNote(text, Baseline["click"], NoteFor(kind));
        if (kind is "com" or "plain")
            text.Should().NotContain("walk failed", "R6: a fault's message is not echoed, only its type");
    }

    [Fact]
    public async Task R6_a_multi_line_message_is_one_line_in_the_note()
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] = new InvalidOperationException("line one\r\nline two\nline three");

        var text = await rig.Act("click", "foreground");

        var note = text[(Baseline["click"].Length + 2)..];
        note.Should().NotContain("\n").And.NotContain("\r");
        note.Should().Contain("line one").And.Contain("line two").And.Contain("line three");
        ShouldBeActionPlusNote(text, Baseline["click"], "line three");
    }

    [Fact]
    public async Task R6_a_huge_message_keeps_the_note_under_2200_characters_and_still_says_the_action_happened()
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] = new InvalidOperationException("walk failed " + new string('x', 10_000));

        var text = await rig.Act("click", "foreground");

        var note = text[(Baseline["click"].Length + 2)..];
        note.Length.Should().BeLessThan(2200, "the message part is ToolErrors.MessageFor's, capped");
        ShouldBeActionPlusNote(text, Baseline["click"], "walk failed");
    }

    [Fact]
    public async Task R6_a_non_caller_facing_failure_names_its_type_and_never_echoes_its_message()
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] =
            new COMException("provider internals: secret detail (wmcp-e5)", unchecked((int)0x80004005));

        var text = await rig.Act("click", "foreground");

        ShouldBeActionPlusNote(text, Baseline["click"], "COMException");
        text.Should().NotContain("secret").And.NotContain("wmcp-e5");
    }

    // ---- R7: a truncated observation says so and names the caller's remedy --------------------

    [Fact]
    public async Task R7_a_truncated_observation_says_so_in_the_header_and_points_at_snapshot()
    {
        // The action tools take no scope, window or max_elements: the remedy is the snapshot tool.
        var rig = new Rig();
        rig.ForegroundSnap = Result(
            windows: [Window("Fg App - Notepad", hwnd: 0x1111, isActive: true)],
            active: Window("Fg App - Notepad", hwnd: 0x1111, isActive: true),
            interactive: [Element(id: "el_40", window: "Fg App - Notepad", name: "File")],
            truncated: true, elementLimit: 500, elementCount: 500);

        var text = await rig.Act("click", "foreground");

        var header = HeaderOf(text, Baseline["click"], SnapshotRenderer.Render(rig.ForegroundSnap));
        header.Should().ContainEquivalentOf("truncated");
        header.Should().Contain("snapshot").And.Contain("max_elements").And.Contain("scope",
            "call snapshot with scope, window or max_elements for the rest");
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task A_cancelled_snapshot_propagates(string tool)
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] = new OperationCanceledException("cancelled (wmcp-e5)");

        Func<Task> act = () => rig.Act(tool, "foreground");

        await act.Should().ThrowAsync<OperationCanceledException>("a cancellation is the caller's decision, not a failed observation");
    }

    [Fact]
    public async Task A_snapshot_cancelled_as_a_TaskCanceledException_propagates_too()
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] = new TaskCanceledException("cancelled (wmcp-e5)");

        Func<Task> act = () => rig.Act("click", "foreground");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task A_cancelled_settle_wait_propagates_and_takes_no_snapshot(string tool)
    {
        var rig = new Rig();
        rig.SettleFailure = new OperationCanceledException("cancelled during settle (wmcp-e5)");

        Func<Task> act = () => rig.Act(tool, "foreground");

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ============================================================================================
    // O7: an action that throws is not observed
    // ============================================================================================

    public static TheoryData<string, string> FailingActions()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { "click", "type", "key", "shortcut", "scroll", "drag", "interact_element" })
            data.Add(tool, "foreground");
        foreach (var tool in new[] { "click", "type", "scroll", "drag", "interact_element" })
            data.Add(tool, "window");
        return data;
    }

    [Theory]
    [MemberData(nameof(FailingActions))]
    public async Task An_action_that_throws_is_not_observed_and_its_exception_propagates_unchanged(string tool, string observe)
    {
        var rig = new Rig();
        var boom = new InvalidOperationException("the action failed (wmcp-e5)");
        rig.ActionFailure = boom;

        Func<Task> act = () => rig.Act(tool, observe);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom,
            "the action's own exception, not a note and not a wrapper");
        rig.Log.Should().NotContain(e => e.StartsWith("settle:", StringComparison.Ordinal), "no settle wait after a failed action");
        rig.Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "nothing is observed after an action that failed");
    }

    // ============================================================================================
    // O8: a refusal that precedes the action observes nothing
    // ============================================================================================

    public static TheoryData<string> PreActionRefusals => new()
    {
        "click_negative_clicks", "click_bad_button", "click_win_modifier", "click_no_target", "click_both_targets",
        "click_offscreen_element", "click_offscreen_element_window",
        "type_negative_pace", "type_bad_caret", "type_offscreen_element",
        "key_zero_repeat", "key_repeat_and_hold", "key_repeat_a_modifier", "key_bad_hold",
        "scroll_shift_wheel_up", "scroll_offscreen_element",
        "drag_one_step", "drag_no_destination", "drag_offscreen_destination",
    };

    private static Task<string> Refused(Rig rig, string row) => row switch
    {
        "click_negative_clicks" => rig.Tools.Click(x: 1, y: 2, clicks: -1, observe: "foreground"),
        "click_bad_button" => rig.Tools.Click(x: 1, y: 2, button: "thumb", observe: "foreground"),
        "click_win_modifier" => rig.Tools.Click(x: 1, y: 2, modifiers: "win", observe: "foreground"),
        "click_no_target" => rig.Tools.Click(observe: "foreground"),
        "click_both_targets" => rig.Tools.Click(x: 1, y: 2, element_id: "el_12", observe: "foreground"),
        "click_offscreen_element" => rig.Tools.Click(element_id: "el_off", observe: "foreground"),
        "click_offscreen_element_window" => rig.Tools.Click(element_id: "el_off", observe: "window"),
        "type_negative_pace" => rig.Tools.Type("hi", pace_ms: -1, observe: "foreground"),
        "type_bad_caret" => rig.Tools.Type("hi", caret: "middle", observe: "foreground"),
        "type_offscreen_element" => rig.Tools.Type("hi", element_id: "el_off", observe: "foreground"),
        "key_zero_repeat" => rig.Tools.Key("enter", repeat: 0, observe: "foreground"),
        "key_repeat_and_hold" => rig.Tools.Key("a", repeat: 2, hold_ms: 10, observe: "foreground"),
        "key_repeat_a_modifier" => rig.Tools.Key("ctrl", repeat: 2, observe: "foreground"),
        "key_bad_hold" => rig.Tools.Key("a", hold_ms: 10001, observe: "foreground"),
        "scroll_shift_wheel_up" => rig.Tools.Scroll("up", shift_wheel: true, observe: "foreground"),
        "scroll_offscreen_element" => rig.Tools.Scroll("down", element_id: "el_off", observe: "foreground"),
        "drag_one_step" => rig.Tools.Drag(to_x: 5, to_y: 5, steps: 1, observe: "foreground"),
        "drag_no_destination" => rig.Tools.Drag(from_x: 1, from_y: 2, observe: "foreground"),
        "drag_offscreen_destination" => rig.Tools.Drag(element_id: "el_off", observe: "foreground"),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no such row"),
    };

    [Theory]
    [MemberData(nameof(PreActionRefusals))]
    public async Task A_refusal_before_the_action_waits_for_nothing_and_takes_no_snapshot(string row)
    {
        var rig = new Rig();

        Func<Task> act = () => Refused(rig, row);

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        ex.Should().Match<Exception>(e => e is ArgumentException || e is InvalidOperationException,
            "the tool's own refusal, unchanged by observe");
        rig.InputSent.Should().BeEmpty("a refusal sends no input");
        rig.Log.Should().NotContain(e => e.StartsWith("settle:", StringComparison.Ordinal));
        rig.Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "nothing happened, so there is nothing to observe");
    }

    // ============================================================================================
    // W1: observe:window needs an element target
    // ============================================================================================

    public static TheoryData<string> WindowWithoutElement => new()
    {
        "key", "shortcut", "click_point", "type_point", "type_at_focus", "scroll_point", "scroll_at_cursor",
        "drag_points", "drag_from_cursor_to_point",
    };

    private static Task<string> WindowWithout(Rig rig, string row) => row switch
    {
        "key" => rig.Tools.Key("enter", observe: "window"),
        "shortcut" => rig.Tools.Shortcut("ctrl+s", observe: "window"),
        "click_point" => rig.Tools.Click(x: 10, y: 20, observe: "window"),
        "type_point" => rig.Tools.Type("hi", x: 10, y: 20, observe: "window"),
        "type_at_focus" => rig.Tools.Type("hi", observe: "window"),
        "scroll_point" => rig.Tools.Scroll("down", x: 10, y: 20, observe: "window"),
        "scroll_at_cursor" => rig.Tools.Scroll("down", observe: "window"),
        "drag_points" => rig.Tools.Drag(from_x: 1, from_y: 2, to_x: 300, to_y: 400, observe: "window"),
        "drag_from_cursor_to_point" => rig.Tools.Drag(to_x: 300, to_y: 400, observe: "window"),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no such row"),
    };

    [Theory]
    [MemberData(nameof(WindowWithoutElement))]
    public async Task Observe_window_without_an_element_target_is_refused_pointing_at_foreground_before_any_input(string row)
    {
        var rig = new Rig();

        Func<Task> act = () => WindowWithout(rig, row);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be("observe");
        ex.Message.Should().Contain("element_id", "window means the window of the target element, so it needs one")
            .And.Contain("foreground", "and the refusal names the way out");
        rig.InputSent.Should().BeEmpty("refused before any input");
        rig.NothingObserved("a refused call observes nothing");
    }

    // ============================================================================================
    // W2: the window is resolved BEFORE the action
    // ============================================================================================

    [Theory]
    [MemberData(nameof(ElementTools))]
    public async Task Observe_window_looks_up_the_elements_window_once_before_any_input(string tool)
    {
        var rig = new Rig();

        await rig.Act(tool, "window");

        rig.Uia.Verify(s => s.GetElementWindowAsync("el_12", It.IsAny<CancellationToken>()), Times.Once);
        rig.Uia.Verify(s => s.GetElementWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var lookup = rig.Log.IndexOf("window-of:el_12");
        var firstInput = rig.Log.FindIndex(IsInput);
        lookup.Should().BeGreaterThanOrEqualTo(0);
        firstInput.Should().BeGreaterThan(lookup, "the element may be gone after the action (an OK button that closes its dialog)");
        rig.Log.Where(e => e != "window-of:el_12").Should().Equal(
            ActionLog[tool].Concat(["settle:250", $"snapshot:Window:{ElementWindow}"]));
    }

    public static TheoryData<string, string> ElementToolsTimesWindowRefusal()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { "click", "type", "scroll", "drag", "interact_element" })
            foreach (var kind in new[] { "expired", "gone", "timeout" })
                data.Add(tool, kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(ElementToolsTimesWindowRefusal))]
    public async Task A_window_lookup_refusal_is_the_tools_refusal_and_no_input_is_sent(string tool, string kind)
    {
        var rig = new Rig();
        Exception refusal = kind switch
        {
            "expired" => new KeyNotFoundException("Element 'el_12' has expired (wmcp-e5)"),
            // PR #43 X1: a hung provider during the lookup is refused before acting, like the others.
            "timeout" => new TimeoutException("window lookup timed out (wmcp-e5)"),
            _ => new InvalidOperationException("Element 'el_12' is no longer available (wmcp-e5)"),
        };
        rig.WindowFailure = refusal;

        Func<Task> act = () => rig.Act(tool, "window");

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(refusal, "the lookup's refusal, unchanged");
        rig.InputSent.Should().BeEmpty("the window is resolved before the action, so its refusal precedes any input");
        rig.Log.Should().NotContain(e => e.StartsWith("settle:", StringComparison.Ordinal));
        rig.Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Drag_observe_window_uses_the_destination_element_when_both_ends_are_elements()
    {
        var rig = new Rig();

        await rig.Tools.Drag(from_element_id: "el_3", element_id: "el_12", observe: "window");

        rig.Uia.Verify(s => s.GetElementWindowAsync("el_12", It.IsAny<CancellationToken>()), Times.Once);
        rig.Uia.Verify(s => s.GetElementWindowAsync("el_3", It.IsAny<CancellationToken>()), Times.Never,
            "the drop target's window is where the result shows");
        rig.Log.Should().Contain($"snapshot:Window:{ElementWindow}");
    }

    [Fact]
    public async Task Drag_observe_window_falls_back_to_the_origin_element_when_the_destination_is_a_point()
    {
        var rig = new Rig();

        await rig.Tools.Drag(from_element_id: "el_3", to_x: 300, to_y: 400, observe: "window");

        rig.Uia.Verify(s => s.GetElementWindowAsync("el_3", It.IsAny<CancellationToken>()), Times.Once);
        rig.Uia.Verify(s => s.GetElementWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var lookup = rig.Log.IndexOf("window-of:el_3");
        rig.Log.FindIndex(IsInput).Should().BeGreaterThan(lookup).And.BeGreaterThanOrEqualTo(0);
        rig.Log.Should().ContainSingle(e => e.StartsWith("snapshot:", StringComparison.Ordinal))
            .Which.Should().Be($"snapshot:Window:{OriginWindow}");
    }

    // ============================================================================================
    // W3: the window snapshot, by handle
    // ============================================================================================

    [Theory]
    [MemberData(nameof(ElementTools))]
    public async Task Observe_window_snapshots_that_window_by_handle_and_says_so_in_the_header(string tool)
    {
        var rig = new Rig();

        var text = await rig.Act(tool, "window");

        var expected = new SnapshotRequest(SnapshotScope.Window, WindowHwnd: ElementWindow, IncludePopups: true);
        rig.Uia.Verify(s => s.SnapshotAsync(expected, It.IsAny<CancellationToken>()), Times.Once,
            "scope window by handle: no title (another window can share it), no tree, the default budget, no DOM");
        rig.Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var header = HeaderOf(text, Baseline[tool], SnapshotRenderer.Render(rig.WindowSnap));
        header.Should().Contain("Observed").And.Contain("window").And.Contain("250 ms");
        header.Should().NotContainEquivalentOf("foreground", "this is the element's window, not the foreground");
        header.Should().MatchRegex(NamesIds).And.MatchRegex(IdsNoLongerValid);
    }

    // ============================================================================================
    // W4: the element's window closed - one foreground snapshot instead
    // ============================================================================================

    [Theory]
    [MemberData(nameof(ElementTools))]
    public async Task A_window_that_closed_falls_back_to_one_foreground_snapshot_and_the_header_says_so(string tool)
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Window] = new KeyNotFoundException($"No top-level window with handle {ElementWindow} (wmcp-e5)");

        var text = await rig.Act(tool, "window");

        rig.Log.Where(e => e != "window-of:el_12").Should().Equal(
            ActionLog[tool].Concat(["settle:250", $"snapshot:Window:{ElementWindow}", "snapshot:Foreground:0"]),
            "one settle wait, the window snapshot, then exactly one foreground snapshot");
        var header = HeaderOf(text, Baseline[tool], SnapshotRenderer.Render(rig.ForegroundSnap));
        header.Should().Contain("Observed").And.Contain("foreground");
        header.Should().MatchRegex("(?i)gone|closed|no longer (open|exists)",
            "the header says the element's window is gone and this is the foreground instead");
        var window = new SnapshotRequest(SnapshotScope.Window, WindowHwnd: ElementWindow, IncludePopups: true);
        var fallback = new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true);
        rig.Uia.Verify(s => s.SnapshotAsync(window, It.IsAny<CancellationToken>()), Times.Once);
        rig.Uia.Verify(s => s.SnapshotAsync(fallback, It.IsAny<CancellationToken>()), Times.Once,
            "R3: the fallback includes the foreground's popups too");
    }

    [Fact]
    public async Task A_failed_fallback_is_the_note_carrying_the_fallbacks_message()
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Window] = new KeyNotFoundException("window closed (wmcp-e5)");
        rig.SnapshotFailure[SnapshotScope.Foreground] = new InvalidOperationException("fallback failed (wmcp-e5)");

        var text = await rig.Act("click", "window");

        ShouldBeActionPlusNote(text, Baseline["click"], "fallback failed (wmcp-e5)");
        rig.Snapshots.Should().Be(2, "the window snapshot and ONE fallback, no more");
    }

    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("timeout")]
    [InlineData("com")]
    public async Task A_window_snapshot_that_fails_another_way_is_the_note_without_a_fallback(string kind)
    {
        // Only "that window is not there" (KeyNotFoundException) is a reason to look at the
        // foreground instead; a walk that failed would only fail again or describe another window.
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Window] = SnapshotFailureOf(kind);

        var text = await rig.Act("click", "window");

        ShouldBeActionPlusNote(text, Baseline["click"], NoteFor(kind));
        rig.Snapshots.Should().Be(1);
        text.Should().NotContainEquivalentOf("gone", "PR #43 P1: only a KeyNotFound means the window closed; a timeout is not that");
    }

    [Fact]
    public async Task A_cancelled_fallback_propagates()
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Window] = new KeyNotFoundException("window closed (wmcp-e5)");
        rig.SnapshotFailure[SnapshotScope.Foreground] = new OperationCanceledException("cancelled (wmcp-e5)");

        Func<Task> act = () => rig.Act("click", "window");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ============================================================================================
    // GREEN: the decisions the contract left open, pinned so a change to them is deliberate
    // ============================================================================================

    [Fact]
    public async Task The_observe_and_settle_refusals_come_before_the_tools_own_argument_checks()
    {
        // Decision 1: observe, settle_ms and (for window) the window lookup are checked first.
        var rig = new Rig();

        Func<Task> badBoth = () => rig.Tools.Click(x: 1, y: 2, button: "thumb", observe: "bogus");
        Func<Task> badSettle = () => rig.Tools.Key("enter", repeat: 0, settle_ms: -1);
        Func<Task> windowNoElement = () => rig.Tools.Key("enter", repeat: 0, observe: "window");

        (await badBoth.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("observe");
        (await badSettle.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("settle_ms");
        (await windowNoElement.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("observe");
        rig.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task With_observe_window_the_window_lookup_comes_before_the_target_is_resolved()
    {
        // Decision 1: so an off-screen element is refused after its window was looked up.
        var rig = new Rig();

        Func<Task> act = () => rig.Tools.Click(element_id: "el_off", observe: "window");

        await act.Should().ThrowAsync<InvalidOperationException>();
        rig.Log.Should().Equal("window-of:el_off", "resolve:el_off");
    }

    [Theory]
    [InlineData(" foreground ")]
    [InlineData("foreground ")]
    [InlineData("\twindow")]
    [InlineData("none ")]
    public async Task Observe_is_not_trimmed(string observe)
    {
        // Decision 2.
        var rig = new Rig();

        Func<Task> act = () => rig.Act("click", observe);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("observe");
        rig.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_observe_is_refused_like_any_other_unknown_value()
    {
        // A client can send JSON null for a string argument; it is not "none".
        var rig = new Rig();

        Func<Task> act = () => rig.Act("click", null!);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.ParamName.Should().Be("observe");
        rig.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_observe_is_echoed_clipped_to_80_characters()
    {
        // Decision 6: a refusal quotes caller text through UiRefusals.Clip.
        var rig = new Rig();
        var junk = new string('q', 500);

        Func<Task> act = () => rig.Act("click", junk);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain(new string('q', 80) + "...").And.NotContain(new string('q', 81));
    }

    [Theory]
    [InlineData("scroll_at_cursor")]
    [InlineData("drag_from_cursor_to_point")]
    public async Task Observe_window_with_a_cursor_target_is_refused_before_the_cursor_is_read(string row)
    {
        // Decision 3.
        var rig = new Rig();

        Func<Task> act = () => WindowWithout(rig, row);

        await act.Should().ThrowAsync<ArgumentException>();
        rig.Log.Should().BeEmpty("not even the cursor position is read");
    }

    [Theory]
    [InlineData("type")]
    [InlineData("shortcut")]
    [InlineData("scroll")]
    [InlineData("drag")]
    [InlineData("interact_element")]
    public async Task The_five_tools_without_a_token_settle_and_snapshot_on_CancellationToken_None(string tool)
    {
        // Decision 4: their parameter lists are pinned without a CancellationToken, so there is no
        // request token to forward. A change here (adding one) has to be deliberate.
        var rig = new Rig();

        // shortcut has no element, so it observes the foreground and looks up no window.
        var window = tool != "shortcut";

        await rig.Act(tool, window ? "window" : "foreground");

        CancellationToken[] lookups = window ? [CancellationToken.None] : [];
        rig.WindowTokens.Should().Equal(lookups);
        rig.SettleTokens.Should().Equal([CancellationToken.None]);
        rig.SnapshotTokens.Should().Equal([CancellationToken.None]);
    }

    [Fact]
    public async Task Click_forwards_its_token_to_the_window_lookup_as_well()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Tools.Click(element_id: "el_12", observe: "window", ct: cts.Token);

        rig.WindowTokens.Should().Equal([cts.Token]);
        rig.SettleTokens.Should().Equal([cts.Token]);
        rig.SnapshotTokens.Should().Equal([cts.Token]);
    }

    [Fact]
    public async Task The_failure_note_is_one_line_in_the_agreed_words()
    {
        // Decision 7.
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Foreground] = new InvalidOperationException("UIA is busy.");

        var text = await rig.Act("click", "foreground");

        text.Should().Be(Baseline["click"] + "\n\n" +
            "Observation failed: UIA is busy. The action above was performed, so do not repeat it; call snapshot to see the result.");
    }

    [Fact]
    public async Task A_hover_only_click_is_observed_too()
    {
        // Decision 8: clicks:0 moves the pointer, which can open a tooltip or a hover menu.
        var rig = new Rig();

        var text = await rig.Tools.Click(element_id: "el_12", clicks: 0, observe: "foreground");

        rig.Log.Should().Equal("resolve:el_12", "hover:120,210", "settle:250", "snapshot:Foreground:0");
        text.Should().StartWith(
            "{\"action\":\"hover\",\"x\":120,\"y\":210,\"button\":\"left\",\"clicks\":0,\"elementId\":\"el_12\",\"name\":\"Save\",\"modifiers\":null}\n\n");
        text.Should().EndWith(SnapshotRenderer.Render(rig.ForegroundSnap));
    }

    // ============================================================================================
    // PR #43 Z3: popup text after the rendered snapshot
    // ============================================================================================

    private const string PopupTextHeading = "Popup text (not controls, no ids):";

    [Theory]
    [InlineData("foreground")]
    [InlineData("window")]
    public async Task Z3_popup_text_follows_the_rendered_snapshot_after_a_blank_line_and_its_heading(string observe)
    {
        var rig = new Rig();
        rig.ForegroundSnap = rig.ForegroundSnap with { PopupText = ["popup: Saved to Documents", "Find: 3 matches"] };
        rig.WindowSnap = rig.WindowSnap with { PopupText = ["popup: Saved to Documents", "Find: 3 matches"] };
        var snap = observe == "foreground" ? rig.ForegroundSnap : rig.WindowSnap;

        var text = await rig.Act("click", observe);

        text.Should().EndWith(SnapshotRenderer.Render(snap) + "\n\n" + PopupTextHeading + "\npopup: Saved to Documents\nFind: 3 matches",
            "the rendered snapshot unchanged, then a blank line, the heading and one line per entry, LF-separated");
        text.Should().StartWith(Baseline["click"] + "\n\n");
    }

    [Fact]
    public async Task Z3_popup_text_follows_the_foreground_fallback_after_the_window_closed_too()
    {
        var rig = new Rig();
        rig.SnapshotFailure[SnapshotScope.Window] = new KeyNotFoundException("window closed (wmcp-e5)");
        rig.ForegroundSnap = rig.ForegroundSnap with { PopupText = ["popup: Saved"] };

        var text = await rig.Act("click", "window");

        text.Split('\n')[2].Should().ContainEquivalentOf("gone");
        text.Should().EndWith(SnapshotRenderer.Render(rig.ForegroundSnap) + "\n\n" + PopupTextHeading + "\npopup: Saved");
    }

    [Fact]
    public async Task Z3_guard_an_empty_popup_text_appends_nothing()
    {
        var rig = new Rig();
        rig.ForegroundSnap = rig.ForegroundSnap with { PopupText = [] };

        var text = await rig.Act("click", "foreground");

        text.Should().EndWith(SnapshotRenderer.Render(rig.ForegroundSnap)).And.NotContain(PopupTextHeading);
    }

    // The null case is every exact-text row above (O5, W3, W4): PopupText defaults to null there.

    // ============================================================================================
    // E-8: the session warning reaches an observation through the shared renderer
    // ============================================================================================

    private const string SecureDesktopNote =
        "A secure desktop has the input (a UAC prompt, or the sign-in or lock screen): the user's desktop cannot be seen or driven until the user answers it. Stop and tell the user.";

    [Theory]
    [MemberData(nameof(SevenTools))]
    public async Task Observe_puts_the_session_warning_directly_under_the_header(string tool)
    {
        // docs/design/E-8-session-state.md: the renderer is shared, so the same first line appears
        // in an E-5 observation - right after the "Observed ..." header, before the Cursor line.
        var rig = new Rig();
        rig.ForegroundSnap = rig.ForegroundSnap with { SessionNote = SecureDesktopNote };
        rig.WindowSnap = rig.WindowSnap with { SessionNote = SecureDesktopNote };

        var text = await rig.Act(tool, "foreground");

        var lines = text.Split('\n');
        int header = Array.FindIndex(lines, l => l.StartsWith("Observed ", StringComparison.Ordinal));
        header.Should().BeGreaterThan(0, "the observation header is there");
        lines[header + 1].Should().Be("Warning: " + SecureDesktopNote, "the warning is the snapshot's first line");
        lines[header + 2].Should().StartWith("Cursor: (", "and the snapshot follows it unchanged");
        lines.Count(l => l.StartsWith("Warning:", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Observe_without_a_session_note_has_no_warning_line()
    {
        var rig = new Rig();

        var text = await rig.Act("key", "foreground");

        text.Should().NotContain("Warning:");
    }

    // ============================================================================================
    // The Delay seam's default
    // ============================================================================================

    [Fact]
    public async Task The_default_settle_wait_is_a_real_wait_that_honours_cancellation()
    {
        // A seam left at a no-op would make every observation a zero-settle one, and every Unit row
        // above replaces it - so the default is pinned on both tool classes.
        foreach (var observation in new[] { InputVerb.Tools().Observation, new UIAutomationTools(new Mock<IUIAutomationService>().Object).Observation })
        {
            using var cts = new CancellationTokenSource();
            var pending = observation.Delay(TimeSpan.FromSeconds(30), cts.Token);

            pending.IsCompleted.Should().BeFalse("a 30 s settle is really waited");
            cts.Cancel();
            Func<Task> act = () => pending;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}

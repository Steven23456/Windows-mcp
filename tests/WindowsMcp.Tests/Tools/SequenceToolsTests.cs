using System.Text.Json;
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
/// E-6: <c>sequence</c> end to end through the REAL tool methods (one <see cref="InputTools"/>, one
/// <see cref="UIAutomationTools"/>) over mocked services - docs/design/E-6-sequence.md "Running",
/// "Result", "Takeover" and "Observation". Everything the services, the indicator and the delay
/// seams saw goes into one ordered <see cref="Rig.Log"/>, so a row can say what ran in which order
/// and, for a refusal or a takeover, that nothing ran at all.
/// <para>
/// No real input: every service is a mock. <c>wait</c> steps really sleep (the tool calls
/// <c>Task.Delay</c> directly), so they use 10 ms.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class SequenceToolsTests
{
    private const string TakeoverPrefix = "The user took over the mouse and keyboard; ";

    private static readonly string[] InputPrefixes =
        ["click:", "hover:", "type:", "key:", "shortcut:", "scroll:", "drag:", "down:", "up:", "interact:"];

    private static string Key(string key) => $$$"""{"tool":"key","args":{"key":"{{{key}}}"}}""";

    private static string Steps(params string[] steps) => "[" + string.Join(",", steps) + "]";

    /// <summary>The mocks behind the sequence, all writing to one <see cref="Log"/>.</summary>
    private sealed class Rig
    {
        public List<string> Log { get; } = [];
        public Mock<IInputService> Input { get; } = new();
        public Mock<IClipboardService> Clipboard { get; } = new();
        public Mock<IUIAutomationService> Uia { get; } = new();
        public Mock<IControlIndicator> Indicator { get; } = new();

        /// <summary>What TakeoverPending answers; flip it from <see cref="After"/> to take over mid-batch.</summary>
        public bool Pending { get; set; }

        /// <summary>When set, reading TakeoverPending throws it.</summary>
        public Exception? PendingFault { get; set; }

        /// <summary>What TakeoverCount answers: the takeovers recorded so far (E-6's epoch).</summary>
        public long Count { get; set; }

        /// <summary>When set, decides per read (1-based) whether reading TakeoverCount throws.</summary>
        public Func<int, bool>? CountFaults { get; set; }

        /// <summary>When set, what TakeoverCount answers per read (1-based), instead of <see cref="Count"/>.</summary>
        public Func<int, long>? CountAt { get; set; }

        private int _countReads;

        /// <summary>A real takeover: the record is pending AND the count moves, as the real indicator does.</summary>
        public void TakeOver()
        {
            Pending = true;
            Count++;
        }

        /// <summary>Thrown by the service call whose log entry is the key, after it is logged.</summary>
        public Dictionary<string, Exception> Throw { get; } = new();

        /// <summary>Runs after every logged service call, before any injected throw.</summary>
        public Action<string>? After { get; set; }

        public List<CancellationToken> KeyTokens { get; } = [];
        public List<CancellationToken> ClickTokens { get; } = [];
        public List<TypeOptions> TypeOptionsSeen { get; } = [];

        public WaitForResult WaitResult { get; set; } = new(true, "element_exists", 40, 1, "found 'File name'");
        public AssertResult AssertOutcome { get; set; } = new("el_5", "exists", true, "exists");

        public SnapshotResult ForegroundSnap { get; } = Result(
            windows: [Window("Save As", hwnd: 0x2222, isActive: true)],
            active: Window("Save As", hwnd: 0x2222, isActive: true),
            interactive: [Element(id: "el_40", window: "Save As", controlType: "Button", name: "Save", action: "click")],
            elementCount: 7);

        public Exception? SnapshotFailure { get; set; }

        public SequenceTools Tools { get; }

        public Rig()
        {
            Input.Setup(s => s.ClickAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, MouseButton b, int c, CancellationToken ct) =>
                {
                    ClickTokens.Add(ct);
                    Fire($"click:{x},{y},{b},{c}");
                    return Task.FromResult(new ClickResult(x, y, b, c));
                });
            Input.Setup(s => s.HoverAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, int _, CancellationToken _) => { Fire($"hover:{x},{y}"); return Task.CompletedTask; });
            Input.Setup(s => s.TypeAsync(It.IsAny<string>(), It.IsAny<TypeOptions>(), It.IsAny<CancellationToken>()))
                .Returns((string text, TypeOptions o, CancellationToken _) =>
                {
                    TypeOptionsSeen.Add(o);
                    Fire($"type:{text}");
                    return Task.FromResult(new TypeResult(text.Length));
                });
            Input.Setup(s => s.PressKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken ct) =>
                {
                    KeyTokens.Add(ct);
                    Fire($"key:{key}");
                    return Task.CompletedTask;
                });
            Input.Setup(s => s.PressShortcutAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string chord, CancellationToken _) => { Fire($"shortcut:{chord}"); return Task.CompletedTask; });
            Input.Setup(s => s.ScrollAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .Returns((int x, int y, string direction, int amount, bool shift, CancellationToken _) =>
                {
                    Fire($"scroll:{x},{y},{direction},{amount},{shift}");
                    return Task.CompletedTask;
                });
            Input.Setup(s => s.DragAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<MouseButton>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns((int fx, int fy, int tx, int ty, MouseButton b, int d, int st, CancellationToken _) =>
                {
                    Fire($"drag:{fx},{fy},{tx},{ty},{b},{d},{st}");
                    return Task.FromResult(new DragResult(fx, fy, tx, ty, b));
                });
            Input.Setup(s => s.KeyDownAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken _) => { Fire($"down:{key}"); return Task.CompletedTask; });
            Input.Setup(s => s.KeyUpAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string key, CancellationToken _) => { Fire($"up:{key}"); return Task.CompletedTask; });
            Input.Setup(s => s.GetCursorPositionAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken _) => { Fire("cursor"); return Task.FromResult(new CursorPosition(500, 600)); });

            Uia.Setup(s => s.GetElementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) => { Fire($"resolve:{id}"); return Task.FromResult(InputVerb.Element(id: id)); });
            Uia.Setup(s => s.InteractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((string id, string action, string? _, CancellationToken _) =>
                {
                    Fire($"interact:{id}:{action}");
                    return Task.FromResult(new InteractResult(id, action, "InvokePattern", null));
                });
            Uia.Setup(s => s.WaitForAsync(It.IsAny<WaitRequest>(), It.IsAny<CancellationToken>()))
                .Returns((WaitRequest r, CancellationToken _) => { Fire($"waitfor:{r.Text}:{r.TimeoutMs}"); return Task.FromResult(WaitResult); });
            Uia.Setup(s => s.AssertElementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((string id, string state, string? _, CancellationToken _) => { Fire($"assert:{id}:{state}"); return Task.FromResult(AssertOutcome); });
            Uia.Setup(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()))
                .Returns((SnapshotRequest r, CancellationToken _) =>
                {
                    Log.Add($"snapshot:{r.Scope}");
                    if (SnapshotFailure is { } e) throw e;
                    return Task.FromResult(ForegroundSnap);
                });
            Uia.Setup(s => s.GetElementWindowAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken _) => { Log.Add($"window-of:{id}"); return Task.FromResult(0x2222L); });

            Indicator.SetupGet(i => i.TakeoverPending).Returns(() =>
            {
                Log.Add("pending?");
                if (PendingFault is { } e) throw e;
                return Pending;
            });
            Indicator.SetupGet(i => i.TakeoverCount).Returns(() =>
            {
                Log.Add("count?");
                int read = ++_countReads;
                if (CountFaults is { } faults && faults(read))
                    throw new InvalidOperationException("injected: reading the count faults");
                return CountAt is { } at ? at(read) : Count;
            });

            Tools = new SequenceTools(Input.Object, Clipboard.Object, Uia.Object, Indicator.Object);
            Tools.InputTools.Delay = (_, _) => { Log.Add("pace"); return Task.CompletedTask; };
            Tools.Observation.Delay = (span, _) => { Log.Add($"settle:{(int)span.TotalMilliseconds}"); return Task.CompletedTask; };
            // A step never observes: if one did, it would show up under its own name.
            Tools.InputTools.Observation.Delay = (_, _) => { Log.Add("step-settle"); return Task.CompletedTask; };
            Tools.UIAutomationTools.Observation.Delay = (_, _) => { Log.Add("step-settle"); return Task.CompletedTask; };
        }

        private void Fire(string entry)
        {
            Log.Add(entry);
            After?.Invoke(entry);
            if (Throw.TryGetValue(entry, out var e)) throw e;
        }

        public Task<string> Run(string steps, string observe = "none", int settle = 250, CancellationToken ct = default) =>
            Tools.Sequence(steps, observe, settle, ct);

        public string[] InputSent => Log.Where(e => InputPrefixes.Any(p => e.StartsWith(p, StringComparison.Ordinal))).ToArray();

        public int Snapshots => Log.Count(e => e.StartsWith("snapshot:", StringComparison.Ordinal));

        /// <summary>Nothing reached any input or UIA service and nothing was observed.</summary>
        public void NothingDone(string because)
        {
            InputSent.Should().BeEmpty(because);
            Input.Invocations.Should().BeEmpty(because);
            Uia.Invocations.Should().BeEmpty(because);
            Log.Should().NotContain(e => e.StartsWith("settle:", StringComparison.Ordinal), because);
        }
    }

    /// <summary>The sequence's JSON: the whole text with observe:none, the part before the observation otherwise.</summary>
    private static JsonElement Json(string text)
    {
        int cut = text.IndexOf("\n\n", StringComparison.Ordinal);
        return InputVerb.Json(cut < 0 ? text : text[..cut]);
    }

    private static JsonElement[] Results(JsonElement root) => root.GetProperty("results").EnumerateArray().ToArray();

    private static bool IsNull(JsonElement root, string name) => InputVerb.Absent(root, name);

    // ---- R: everything checkable is refused before step 0, with nothing done -------------------------

    public static TheoryData<string, string> BadLastStep => new()
    {
        { """{"tool":"powershell","args":{"command":"Get-Date"}}""", "powershell" },
        { """{"tool":"sequence","args":{"steps_json":"[]"}}""", "sequence" },
        { """{"tool":"type","args":{"txt":"x"}}""", "txt" },
        { """{"tool":"click","args":{"x":1,"y":2,"observe":"foreground"}}""", "observe" },
        { """{"tool":"click","args":{"x":"1","y":2}}""", "x" },
        { """{"tool":"type","args":{}}""", "text" },
        { """{"tool":"key","args":[]}""", "args" },
        { """{"tool":"key","args":{"key":"a"},"extra":1}""", "extra" },
        { "5", "steps_json[2]" },
    };

    [Theory]
    [MemberData(nameof(BadLastStep))]
    public async Task A_refused_last_step_refuses_the_batch_before_any_input(string badStep, string fragment)
    {
        var rig = new Rig();

        var act = () => rig.Run(Steps(Key("a"), """{"tool":"click","args":{"x":10,"y":20}}""", badStep), observe: "foreground");

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Message.Should().Contain("steps_json[2]", "the refusal names the bad step");
        ex.Message.Should().Contain(fragment);
        rig.NothingDone("the whole array is validated before step 0 runs - not even the valid steps before the bad one");
        rig.Snapshots.Should().Be(0, "a refused batch is never observed");
    }

    [Fact]
    public async Task Twenty_one_steps_are_refused_with_nothing_done()
    {
        var rig = new Rig();

        var act = () => rig.Run(Steps(Enumerable.Repeat(Key("a"), 21).ToArray()));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("20");
        rig.NothingDone("the step cap is checked before anything runs");
    }

    [Theory]
    [InlineData("window", 250, "observe")]       // a sequence has no single target element
    [InlineData("WINDOW", 250, "observe")]
    [InlineData("bogus", 250, "observe")]
    [InlineData("none", -1, "settle_ms")]        // range-checked always, even with observe:none
    [InlineData("none", 5001, "settle_ms")]
    [InlineData("foreground", -1, "settle_ms")]
    [InlineData("foreground", 5001, "settle_ms")]
    public async Task A_bad_observe_or_settle_ms_is_refused_before_any_step(string observe, int settle, string parameter)
    {
        var rig = new Rig();

        var act = () => rig.Run(Steps(Key("a"), Key("b")), observe, settle);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.ParamName.Should().Be(parameter);
        rig.NothingDone("observe and settle_ms are refused before step 0 runs");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5000)]
    public async Task Settle_ms_at_either_end_of_its_range_is_accepted(int settle)
    {
        var rig = new Rig();

        var text = await rig.Run(Steps(Key("a")), "foreground", settle);

        Json(text).GetProperty("completed").GetInt32().Should().Be(1);
    }

    // ---- running: in order, through the real tool methods -------------------------------------------

    [Fact]
    public async Task Every_allowlisted_tool_runs_in_order_through_its_real_method()
    {
        var rig = new Rig();
        var steps = Steps(
            """{"tool":"shortcut","args":{"shortcut":"ctrl+shift+s"}}""",
            """{"tool":"click","args":{"x":10,"y":20,"button":"right","clicks":2}}""",
            """{"tool":"type","args":{"text":"report.txt","press_enter":true}}""",
            """{"tool":"key","args":{"key":"tab","repeat":2}}""",
            """{"tool":"scroll","args":{"direction":"down","x":5,"y":6,"amount":4}}""",
            """{"tool":"drag","args":{"from_x":1,"from_y":2,"to_x":3,"to_y":4,"duration_ms":0,"steps":2}}""",
            """{"tool":"interact_element","args":{"element_id":"el_5","action":"invoke"}}""",
            """{"tool":"wait","args":{"seconds":0.01}}""",
            """{"tool":"wait_for","args":{"text":"File name","timeout_ms":0}}""",
            """{"tool":"assert_element","args":{"element_id":"el_5","state":"exists"}}""");

        var root = Json(await rig.Run(steps));

        rig.Log.Where(e => !IsIndicatorRead(e)).Should().Equal(
            "shortcut:ctrl+shift+s",
            "click:10,20,Right,2",
            "type:report.txt",
            "key:tab", "pace", "key:tab",           // key's own repeat, with its own pace seam
            "scroll:5,6,down,4,False",
            "drag:1,2,3,4,Left,0,2",
            "interact:el_5:invoke",
            "waitfor:File name:0",
            "assert:el_5:exists");
        rig.TypeOptionsSeen.Should().Equal([new TypeOptions(false, CaretPosition.Idle, true, 5)],
            "the step's press_enter, and type's own defaults for the rest");
        rig.Log.Should().NotContain("step-settle", "a step never observes on its own");
        rig.Uia.Verify(s => s.SnapshotAsync(It.IsAny<SnapshotRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        root.GetProperty("count").GetInt32().Should().Be(10);
        root.GetProperty("completed").GetInt32().Should().Be(10);
        var results = Results(root);
        results.Select(r => r.GetProperty("index").GetInt32()).Should().Equal(Enumerable.Range(0, 10));
        results.Select(r => r.GetProperty("tool").GetString()).Should().Equal(
            "shortcut", "click", "type", "key", "scroll", "drag", "interact_element", "wait", "wait_for", "assert_element");
        results.Should().OnlyContain(r => r.GetProperty("ok").GetBoolean());
        results[0].GetProperty("result").GetString().Should().Be("pressed ctrl+shift+s", "a plain-text result is embedded as the string");
        results[1].GetProperty("result").GetProperty("action").GetString().Should().Be("click", "a JSON result is embedded as JSON, not as a string");
        results[1].GetProperty("result").GetProperty("button").GetString().Should().Be("right");
        results[1].GetProperty("result").GetProperty("clicks").GetInt32().Should().Be(2);
        results[2].GetProperty("result").GetProperty("typed").GetInt32().Should().Be(10);
        results[3].GetProperty("result").GetString().Should().Be("pressed tab 2 times");
        results[4].GetProperty("result").GetProperty("amount").GetInt32().Should().Be(4);
        results[5].GetProperty("result").GetProperty("toX").GetInt32().Should().Be(3);
        results[6].GetProperty("result").GetProperty("Method").GetString().Should().Be("InvokePattern");
        results[7].GetProperty("result").GetProperty("waited").GetDouble().Should().Be(0.01);
        results[8].GetProperty("result").GetProperty("Satisfied").GetBoolean().Should().BeTrue();
        results[9].GetProperty("result").GetString().Should().Be("PASS");
    }

    [Fact]
    public async Task An_all_ok_batch_has_completed_equal_to_count_and_null_stop_fields()
    {
        var rig = new Rig();

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        root.GetProperty("count").GetInt32().Should().Be(3);
        root.GetProperty("completed").GetInt32().Should().Be(3);
        Results(root).Should().HaveCount(3);
        IsNull(root, "failedIndex").Should().BeTrue();
        IsNull(root, "error").Should().BeTrue();
        IsNull(root, "stoppedBy").Should().BeTrue();
        Results(root).Should().OnlyContain(r => IsNull(r, "error"), "a step that succeeded carries no error");
        rig.InputSent.Should().Equal("key:a", "key:b", "key:c");
    }

    [Fact]
    public async Task Steps_json_given_as_a_string_holding_the_array_runs()
    {
        var rig = new Rig();

        var root = Json(await rig.Run(JsonSerializer.Serialize(Steps(Key("a"), Key("b")))));

        root.GetProperty("completed").GetInt32().Should().Be(2);
        rig.InputSent.Should().Equal("key:a", "key:b");
    }

    [Fact]
    public async Task The_sequences_token_reaches_the_steps_that_take_one()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        await rig.Run(Steps(Key("a"), """{"tool":"click","args":{"x":1,"y":2}}"""), ct: cts.Token);

        rig.KeyTokens.Should().Equal([cts.Token], "key passes its token to the service; in a sequence it is the sequence's");
        rig.ClickTokens.Should().Equal([cts.Token], "click passes its token to the service too");
    }

    // ---- stop at the first step that throws ----------------------------------------------------------

    [Fact]
    public async Task The_batch_stops_at_the_first_throwing_step_with_the_results_so_far()
    {
        var rig = new Rig();
        rig.Throw["click:10,20,Left,1"] = new InvalidOperationException("the target app refused the click");

        var root = Json(await rig.Run(Steps(Key("a"), """{"tool":"click","args":{"x":10,"y":20}}""", Key("c"))));

        root.GetProperty("count").GetInt32().Should().Be(3);
        root.GetProperty("completed").GetInt32().Should().Be(1, "completed counts the steps that ran and succeeded");
        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("stoppedBy").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Be("the target app refused the click");
        var results = Results(root);
        results.Should().HaveCount(2, "one entry per step that was attempted; step 2 was not");
        results[0].GetProperty("ok").GetBoolean().Should().BeTrue();
        results[1].GetProperty("index").GetInt32().Should().Be(1);
        results[1].GetProperty("tool").GetString().Should().Be("click");
        results[1].GetProperty("ok").GetBoolean().Should().BeFalse();
        results[1].GetProperty("error").GetString().Should().Be("the target app refused the click");
        IsNull(results[1], "result").Should().BeTrue("a step that threw has error instead of result");
        rig.InputSent.Should().Equal(["key:a", "click:10,20,Left,1"], "the step after the failure never runs");
    }

    public static TheoryData<Exception> CallerFacing => new()
    {
        new InvalidOperationException("refused: the window is gone"),
        new ArgumentException("Unknown thing 'x'"),
        new KeyNotFoundException("Element el_9 is no longer available"),
        new TimeoutException("the app did not answer in 5 s"),
        new UnauthorizedAccessException("access denied to the elevated window"),
        new IOException("in use"),
        new FileNotFoundException("C:/nope.txt was not found"),
    };

    [Theory]
    [MemberData(nameof(CallerFacing))]
    public async Task A_caller_facing_failure_reports_its_own_message(Exception failure)
    {
        var rig = new Rig();
        rig.Throw["key:b"] = failure;

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"))));

        root.GetProperty("error").GetString().Should().Be(failure.Message, "ToolErrors.MessageFor of a caller-facing failure is its message");
        Results(root)[1].GetProperty("error").GetString().Should().Be(failure.Message);
        root.GetProperty("stoppedBy").GetString().Should().Be("error");
    }

    public static TheoryData<Exception, string> NotCallerFacing => new()
    {
        { new NullReferenceException("secret: _cache was null at C:/src/internal.cs"), "NullReferenceException" },
        { new NotSupportedException("secret: provider vtable slot 7"), "NotSupportedException" },
        { new System.Runtime.InteropServices.COMException("secret: 0x80040201"), "COMException" },
        { new Exception("secret: bare"), "Exception" },
        { new IndexOutOfRangeException("secret: index 4"), "IndexOutOfRangeException" },
    };

    [Theory]
    [MemberData(nameof(NotCallerFacing))]
    public async Task An_unexpected_fault_is_masked_as_the_filter_masks_it(Exception failure, string typeName)
    {
        var rig = new Rig();
        rig.Throw["key:b"] = failure;

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        var expected = $"An unexpected fault ({typeName}).";
        root.GetProperty("error").GetString().Should().Be(expected, "a sequence leaks nothing a single call would not");
        Results(root)[1].GetProperty("error").GetString().Should().Be(expected);
        root.GetRawText().Should().NotContain("secret", "the fault's own message never reaches the caller");
        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("stoppedBy").GetString().Should().Be("error");
        rig.InputSent.Should().NotContain("key:c");
    }

    [Fact]
    public async Task A_caller_facing_message_is_capped_like_the_filter_caps_it()
    {
        var rig = new Rig();
        var failure = new InvalidOperationException(new string('x', 3000));
        rig.Throw["key:a"] = failure;

        var root = Json(await rig.Run(Steps(Key("a"))));

        root.GetProperty("error").GetString().Should().Be(ToolErrors.MessageFor(failure));
        root.GetProperty("error").GetString()!.Length.Should().BeLessThanOrEqualTo(ToolErrors.MaxMessageLength);
    }

    [Fact]
    public async Task A_value_only_the_tool_can_judge_is_that_steps_failure_with_the_tools_own_refusal()
    {
        var rig = new Rig();

        var root = Json(await rig.Run(Steps(Key("a"), """{"tool":"click","args":{"x":1,"y":2,"button":"bogus"}}""", Key("c"))));

        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("stoppedBy").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("Unknown button 'bogus'", "the same refusal the standalone click gives");
        rig.InputSent.Should().Equal(["key:a"], "click refused before sending anything, and the batch stopped");
    }

    [Fact]
    public async Task A_wait_step_out_of_range_fails_when_it_runs()
    {
        var rig = new Rig();

        var root = Json(await rig.Run(Steps("""{"tool":"wait","args":{"seconds":0}}""", Key("b"))));

        root.GetProperty("failedIndex").GetInt32().Should().Be(0);
        root.GetProperty("error").GetString().Should().Contain("seconds must be more than 0");
        root.GetProperty("completed").GetInt32().Should().Be(0);
        rig.InputSent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_number_beyond_double_range_is_left_to_the_tool_and_fails_its_step()
    {
        // System.Text.Json reads 1e400 as a double (infinity), for the SDK's binding of a standalone
        // wait as much as for the sequence's, so it is not a JSON-type refusal: wait's own range
        // check decides it when the step runs - exactly as the standalone call would.
        var rig = new Rig();

        var root = Json(await rig.Run(Steps(Key("a"), """{"tool":"wait","args":{"seconds":1e400}}""", Key("c"))));

        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("stoppedBy").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Contain("seconds must be more than 0 and at most 60");
        rig.InputSent.Should().Equal(["key:a"]);
    }

    [Fact]
    public async Task An_expired_element_id_fails_its_step()
    {
        var rig = new Rig();
        rig.Throw["resolve:el_9"] = new KeyNotFoundException("Element el_9 is no longer available; take a new snapshot.");

        var root = Json(await rig.Run(Steps(Key("a"), """{"tool":"click","args":{"element_id":"el_9"}}""")));

        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("error").GetString().Should().Be("Element el_9 is no longer available; take a new snapshot.");
    }

    // ---- cancellation: the caller's, and the kind the caller did not ask for ---------------------------

    public static TheoryData<Exception, string> UnaskedCancellations
    {
        get
        {
            using var other = new CancellationTokenSource();
            other.Cancel();
            return new()
            {
                { new OperationCanceledException("secret: a service gave up after its own timeout"), "OperationCanceledException" },
                { new TaskCanceledException("secret: an HttpClient timeout"), "TaskCanceledException" },
                { new OperationCanceledException("secret: someone else's token", other.Token), "OperationCanceledException" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(UnaskedCancellations))]
    public async Task An_OperationCanceledException_the_caller_did_not_ask_for_is_an_ordinary_step_failure(Exception failure, string typeName)
    {
        // E-6 round 2 (F4): "An OperationCanceledException from a step while the caller's token is not
        // cancelled (a timeout inside a service) is an ordinary step failure: stoppedBy:"error", masked
        // like any unexpected fault, with the results so far kept." Reverses the RED round's
        // "propagates" rows: propagating would throw away the steps that already happened.
        var rig = new Rig();
        rig.Throw["key:b"] = failure;

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        root.GetProperty("count").GetInt32().Should().Be(3);
        root.GetProperty("completed").GetInt32().Should().Be(1);
        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("stoppedBy").GetString().Should().Be("error");
        var expected = $"An unexpected fault ({typeName}).";
        root.GetProperty("error").GetString().Should().Be(expected, "masked like any unexpected fault");
        var results = Results(root);
        results.Should().HaveCount(2, "the results so far are kept");
        results[0].GetProperty("ok").GetBoolean().Should().BeTrue();
        results[1].GetProperty("ok").GetBoolean().Should().BeFalse();
        results[1].GetProperty("error").GetString().Should().Be(expected);
        root.GetRawText().Should().NotContain("secret");
        rig.InputSent.Should().Equal(["key:a", "key:b"], "the step after the failure never runs");
    }

    [Fact]
    public async Task An_OperationCanceledException_from_a_step_while_the_caller_has_cancelled_propagates()
    {
        // The true-cancellation counterpart: the caller's token IS cancelled when the step throws.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.After = entry => { if (entry == "key:b") cts.Cancel(); };
        rig.Throw["key:b"] = new OperationCanceledException(cts.Token);

        var act = () => rig.Run(Steps(Key("a"), Key("b"), Key("c")), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the caller cancelled: the call ends, as it does for click and key");
        rig.InputSent.Should().Equal(["key:a", "key:b"]);
    }

    [Fact]
    public async Task Cancelling_during_a_wait_step_ends_the_call()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var act = () => rig.Run(Steps("""{"tool":"wait","args":{"seconds":5}}""", Key("b")), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("wait takes the sequence's token");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "the wait was cut short, not run to its end");
        rig.InputSent.Should().BeEmpty();
    }

    // ---- wait_for and assert_element stop the batch ---------------------------------------------------

    [Fact]
    public async Task An_unsatisfied_wait_for_stops_the_batch()
    {
        var rig = new Rig { WaitResult = new WaitForResult(false, "element_exists", 10000, 21, "no element named 'File name' after 21 attempts") };

        var root = Json(await rig.Run(Steps(
            """{"tool":"shortcut","args":{"shortcut":"ctrl+shift+s"}}""",
            """{"tool":"wait_for","args":{"text":"File name","condition":"element_exists"}}""",
            """{"tool":"type","args":{"text":"report.txt","press_enter":true}}""")));

        root.GetProperty("count").GetInt32().Should().Be(3);
        root.GetProperty("completed").GetInt32().Should().Be(1);
        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("stoppedBy").GetString().Should().Be("wait_for");
        root.GetProperty("error").GetString().Should().StartWith("wait_for was not satisfied")
            .And.Contain("no element named 'File name' after 21 attempts", "the error carries the wait's last Detail");
        var results = Results(root);
        results.Should().HaveCount(2);
        results[1].GetProperty("ok").GetBoolean().Should().BeFalse();
        results[1].GetProperty("result").GetProperty("Satisfied").GetBoolean().Should().BeFalse("the wait's own result is embedded as JSON");
        rig.InputSent.Should().Equal(["shortcut:ctrl+shift+s"], "the type after the unsatisfied wait never runs");
    }

    [Fact]
    public async Task A_satisfied_wait_for_lets_the_batch_go_on()
    {
        var rig = new Rig();

        var root = Json(await rig.Run(Steps("""{"tool":"wait_for","args":{"text":"File name"}}""", Key("b"))));

        root.GetProperty("completed").GetInt32().Should().Be(2);
        IsNull(root, "stoppedBy").Should().BeTrue();
        rig.InputSent.Should().Equal("key:b");
    }

    [Fact]
    public async Task A_failing_assert_element_stops_the_batch()
    {
        var rig = new Rig { AssertOutcome = new AssertResult("el_5", "checked", false, "toggle state Off") };

        var root = Json(await rig.Run(Steps(
            Key("a"),
            """{"tool":"assert_element","args":{"element_id":"el_5","state":"checked"}}""",
            Key("c"))));

        root.GetProperty("completed").GetInt32().Should().Be(1);
        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("stoppedBy").GetString().Should().Be("assert_element");
        root.GetProperty("error").GetString().Should().Contain("FAIL: checked — observed toggle state Off");
        var results = Results(root);
        results.Should().HaveCount(2);
        results[1].GetProperty("ok").GetBoolean().Should().BeFalse();
        results[1].GetProperty("result").GetString().Should().Be("FAIL: checked — observed toggle state Off",
            "assert_element's result is text, so it is embedded as the string");
        rig.InputSent.Should().Equal(["key:a"], "the step after the failed assertion never runs");
    }

    // ---- takeover --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, "steps 0..2 were not run.")]
    [InlineData(1, "step 0 was not run.")]
    public async Task A_takeover_pending_before_step_0_runs_nothing(int count, string tail)
    {
        // Made while the indicator was fading after the previous call: a single tool would act once
        // and then be told; a sequence would act up to twenty times.
        var rig = new Rig { Pending = true };

        var root = Json(await rig.Run(Steps(Enumerable.Range(0, count).Select(i => Key(((char)('a' + i)).ToString())).ToArray())));

        rig.InputSent.Should().BeEmpty("the flag is read before step 0 too");
        root.GetProperty("count").GetInt32().Should().Be(count);
        root.GetProperty("completed").GetInt32().Should().Be(0);
        Results(root).Should().BeEmpty("no step was attempted");
        IsNull(root, "failedIndex").Should().BeTrue("no step failed");
        root.GetProperty("stoppedBy").GetString().Should().Be("takeover");
        root.GetProperty("error").GetString().Should().Be(TakeoverPrefix + tail);
        rig.Indicator.Verify(i => i.ConsumeTakeover(), Times.Never, "the record is left for the call-tool filter's note");
    }

    [Theory]
    [InlineData(4, "step 3 was not run.")]
    [InlineData(5, "steps 3..4 were not run.")]
    public async Task A_takeover_between_steps_2_and_3_leaves_the_rest_unrun(int count, string tail)
    {
        var rig = new Rig();
        rig.After = entry => { if (entry == "key:c") rig.Pending = true; };   // the user presses the hotkey during step 2

        var root = Json(await rig.Run(Steps(Enumerable.Range(0, count).Select(i => Key(((char)('a' + i)).ToString())).ToArray())));

        rig.InputSent.Should().Equal(["key:a", "key:b", "key:c"], "step 2 runs to its end; step 3 is never sent");
        root.GetProperty("count").GetInt32().Should().Be(count);
        root.GetProperty("completed").GetInt32().Should().Be(3);
        var results = Results(root);
        results.Should().HaveCount(3);
        results.Should().OnlyContain(r => r.GetProperty("ok").GetBoolean(), "the steps that ran succeeded");
        IsNull(root, "failedIndex").Should().BeTrue("a takeover is not a failed step");
        root.GetProperty("stoppedBy").GetString().Should().Be("takeover");
        root.GetProperty("error").GetString().Should().Be(TakeoverPrefix + tail);
        rig.Indicator.Verify(i => i.ConsumeTakeover(), Times.Never);
    }

    [Fact]
    public async Task The_takeover_flag_is_read_before_every_step()
    {
        var rig = new Rig();

        await rig.Run(Steps(Key("a"), Key("b"), Key("c")));

        // Immediately before each step there are only indicator reads, and TakeoverPending is among
        // them (E-6 round 2: the count may be read beside it, in either order).
        int previous = -1;
        foreach (var key in new[] { "key:a", "key:b", "key:c" })
        {
            int at = rig.Log.IndexOf(key);
            at.Should().BeGreaterThan(previous, "{0} ran, in order", key);
            var reads = rig.Log.Skip(previous + 1).Take(at - previous - 1).Reverse().TakeWhile(IsIndicatorRead).ToArray();
            reads.Should().Contain("pending?", "TakeoverPending is read immediately before {0}", key);
            previous = at;
        }
    }

    private static bool IsIndicatorRead(string entry) => entry is "pending?" or "count?";

    [Fact]
    public async Task A_takeover_during_the_last_step_does_not_mark_a_complete_batch_as_stopped()
    {
        var rig = new Rig();
        rig.After = entry => { if (entry == "key:c") rig.Pending = true; };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        root.GetProperty("completed").GetInt32().Should().Be(3);
        IsNull(root, "stoppedBy").Should().BeTrue("every step ran; the filter's note tells the caller about the takeover");
        IsNull(root, "error").Should().BeTrue();
        rig.Indicator.Verify(i => i.ConsumeTakeover(), Times.Never);
    }

    [Fact]
    public async Task A_step_that_fails_while_the_user_takes_over_is_reported_as_the_failure()
    {
        var rig = new Rig();
        rig.After = entry => { if (entry == "key:b") rig.Pending = true; };
        rig.Throw["key:b"] = new InvalidOperationException("refused");

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        root.GetProperty("stoppedBy").GetString().Should().Be("error", "step 1 failed before the next takeover check");
        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("error").GetString().Should().Be("refused");
    }

    [Fact]
    public async Task An_indicator_that_faults_reading_the_flag_does_not_stop_the_batch()
    {
        var rig = new Rig { PendingFault = new InvalidOperationException("injected: the indicator faults") };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        rig.InputSent.Should().Equal(["key:a", "key:b", "key:c"], "a fault reading the flag counts as no takeover");
        root.GetProperty("completed").GetInt32().Should().Be(3);
        IsNull(root, "stoppedBy").Should().BeTrue();
        IsNull(root, "error").Should().BeTrue();
    }

    // ---- takeover epoch (E-6 round 2, F2): a record someone else consumed still stops the batch ---------

    [Fact]
    public async Task The_takeover_count_is_read_before_the_first_step()
    {
        var rig = new Rig();

        await rig.Run(Steps(Key("a"), Key("b")));

        int first = rig.Log.IndexOf("key:a");
        rig.Log.Take(first).Should().Contain("count?", "the epoch is taken when the batch starts, before step 0 can change it");
    }

    [Theory]
    [InlineData(3, "steps 1..2 were not run.")]
    [InlineData(2, "step 1 was not run.")]
    public async Task A_takeover_another_call_consumed_before_the_next_step_still_stops_the_batch(int count, string tail)
    {
        // Recorded during step 0, then emptied from the one pending slot by another call's filter (a
        // concurrent snapshot, a second sequence) before step 1: TakeoverPending is false again, the
        // count is not.
        var rig = new Rig();
        rig.After = entry => { if (entry == "key:a") rig.Count++; };

        var root = Json(await rig.Run(Steps(Enumerable.Range(0, count).Select(i => Key(((char)('a' + i)).ToString())).ToArray())));

        rig.InputSent.Should().Equal(["key:a"], "no step after the takeover is sent, although nothing is pending any more");
        root.GetProperty("completed").GetInt32().Should().Be(1);
        Results(root).Should().HaveCount(1);
        IsNull(root, "failedIndex").Should().BeTrue("a takeover is not a failed step");
        root.GetProperty("stoppedBy").GetString().Should().Be("takeover", "the same stop as when the record is still pending");
        root.GetProperty("error").GetString().Should().Be(TakeoverPrefix + tail);
        rig.Indicator.Verify(i => i.ConsumeTakeover(), Times.Never);
    }

    [Fact]
    public async Task A_takeover_with_the_count_moving_too_stops_exactly_as_before()
    {
        var rig = new Rig();
        rig.After = entry => { if (entry == "key:b") rig.TakeOver(); };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"), Key("d"))));

        rig.InputSent.Should().Equal(["key:a", "key:b"]);
        root.GetProperty("stoppedBy").GetString().Should().Be("takeover");
        root.GetProperty("error").GetString().Should().Be(TakeoverPrefix + "steps 2..3 were not run.");
    }

    [Fact]
    public async Task Takeovers_counted_before_the_batch_started_do_not_stop_it()
    {
        // The comparison is against the first reading, not against 0: seven takeovers earlier in the
        // session, all delivered, nothing pending.
        var rig = new Rig { Count = 7 };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        rig.InputSent.Should().Equal(["key:a", "key:b", "key:c"]);
        root.GetProperty("completed").GetInt32().Should().Be(3);
        IsNull(root, "stoppedBy").Should().BeTrue();
    }

    [Theory]
    [InlineData("every read")]
    [InlineData("every read after the first")]
    [InlineData("only the first read")]
    public async Task A_fault_reading_the_takeover_count_counts_as_no_takeover(string which)
    {
        var rig = new Rig
        {
            CountFaults = which switch
            {
                "every read" => _ => true,
                "every read after the first" => n => n > 1,
                _ => n => n == 1,
            },
        };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        rig.InputSent.Should().Equal(["key:a", "key:b", "key:c"], "the indicator can never fail a tool, nor stop one by faulting");
        root.GetProperty("completed").GetInt32().Should().Be(3);
        IsNull(root, "stoppedBy").Should().BeTrue();
        IsNull(root, "error").Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]   // the indicator was disposed mid-batch: every member reads 0 from then on
    [InlineData(2)]   // any decrease: not a takeover
    public async Task A_takeover_count_that_shrinks_after_the_first_reading_does_not_stop_the_batch(long later)
    {
        // E-6 round 3: the count only grows while the indicator lives, so only a count that has GROWN
        // since the first reading is a takeover. A disposed indicator answers 0, and must not turn a
        // shutdown race into stoppedBy:"takeover".
        var rig = new Rig { CountAt = read => read == 1 ? 3 : later };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        rig.InputSent.Should().Equal(["key:a", "key:b", "key:c"], "3 then {0} is not a takeover", later);
        root.GetProperty("completed").GetInt32().Should().Be(3);
        IsNull(root, "stoppedBy").Should().BeTrue();
        IsNull(root, "error").Should().BeTrue();
    }

    [Fact]
    public async Task A_takeover_count_that_grows_after_the_first_reading_stops_the_batch_before_the_next_step()
    {
        // The sibling: 3 at the start, 4 on every later read (a takeover between the first reading and
        // step 0, already consumed elsewhere, so nothing is pending).
        var rig = new Rig { CountAt = read => read == 1 ? 3 : 4 };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"), Key("c"))));

        rig.InputSent.Should().BeEmpty("the count grew before step 0 was checked");
        root.GetProperty("stoppedBy").GetString().Should().Be("takeover");
        IsNull(root, "failedIndex").Should().BeTrue();
        root.GetProperty("error").GetString().Should().Be(TakeoverPrefix + "steps 0..2 were not run.");
    }

    [Fact]
    public async Task A_faulting_count_does_not_hide_a_pending_takeover()
    {
        var rig = new Rig { CountFaults = _ => true };
        rig.After = entry => { if (entry == "key:a") rig.Pending = true; };

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"))));

        rig.InputSent.Should().Equal(["key:a"], "TakeoverPending alone still stops the batch");
        root.GetProperty("stoppedBy").GetString().Should().Be("takeover");
    }

    /// <summary>An <see cref="IControlIndicator"/> over the REAL state core, with no windows: what two calls share.</summary>
    private sealed class StateBackedIndicator(WindowsMcp.Services.ControlIndicatorState state) : IControlIndicator
    {
        public void BeginAction() => state.Begin();
        public void EndAction() => state.End();
        public bool IsVisible => state.ShouldShow;
        public bool IsCaptureExcluded => true;
        public IDisposable SuspendForCapture() => Mock.Of<IDisposable>();
        public TakeoverRecord? ConsumeTakeover() => state.Consume();
        public bool TakeoverPending => state.TakeoverPending;
        public long TakeoverCount => state.TakeoverCount;
    }

    [Fact]
    public async Task Two_sequences_sharing_one_indicator_both_stop_when_the_takeover_is_consumed_between_their_steps()
    {
        // Both batches are in flight (both filters began); the user takes over during A's step 0
        // while B is inside its own step 0; another call's filter consumes the record before either
        // checks again. Neither may send a further step.
        var state = new WindowsMcp.Services.ControlIndicatorState(TimeSpan.FromSeconds(30));
        var indicator = new StateBackedIndicator(state);
        indicator.BeginAction();
        indicator.BeginAction();
        var sentA = new List<string>();
        var sentB = new List<string>();
        var bInStep0 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var inputA = new Mock<IInputService>();
        inputA.Setup(i => i.PressKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken _) =>
            {
                lock (sentA) sentA.Add(key);
                if (key == "a")
                {
                    state.Takeover("Ctrl+Alt+Shift+Backspace").Should().NotBeNull("precondition: the indicator was up");
                    indicator.ConsumeTakeover().Should().NotBeNull("precondition: another call's filter takes the record");
                    releaseB.SetResult();
                }
                return Task.CompletedTask;
            });
        var inputB = new Mock<IInputService>();
        inputB.Setup(i => i.PressKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string key, CancellationToken _) =>
            {
                lock (sentB) sentB.Add(key);
                if (key != "a") return Task.CompletedTask;
                bInStep0.SetResult();
                return releaseB.Task;
            });
        var a = new SequenceTools(inputA.Object, new Mock<IClipboardService>().Object, new Mock<IUIAutomationService>().Object, indicator);
        var b = new SequenceTools(inputB.Object, new Mock<IClipboardService>().Object, new Mock<IUIAutomationService>().Object, indicator);
        var steps = Steps(Key("a"), Key("b"), Key("c"));

        var runB = b.Sequence(steps);
        (await Task.WhenAny(bInStep0.Task, Task.Delay(5000))).Should().BeSameAs(bInStep0.Task, "precondition: B is inside step 0");
        var rootA = Json(await a.Sequence(steps));
        var finishedB = await Task.WhenAny(runB, Task.Delay(5000));
        finishedB.Should().BeSameAs(runB, "B finishes once its step 0 is released");
        var rootB = Json(await runB);

        sentA.Should().Equal(["a"], "A stops before its step 1");
        sentB.Should().Equal(["a"], "B stops before its step 1 too, although the record is gone");
        rootA.GetProperty("stoppedBy").GetString().Should().Be("takeover");
        rootB.GetProperty("stoppedBy").GetString().Should().Be("takeover");
        IsNull(rootA, "failedIndex").Should().BeTrue();
        IsNull(rootB, "failedIndex").Should().BeTrue();
        rootB.GetProperty("error").GetString().Should().Be(TakeoverPrefix + "steps 1..2 were not run.");
    }

    // ---- observation -----------------------------------------------------------------------------------

    [Fact]
    public async Task Observe_foreground_appends_one_observation_after_a_complete_batch()
    {
        var rig = new Rig();

        var text = await rig.Run(Steps(Key("a"), Key("b"), Key("c")), "foreground", 300);

        var lastStep = rig.Log.LastIndexOf("key:c");
        rig.Log.Skip(lastStep + 1).Where(e => !IsIndicatorRead(e)).Should().Equal(["settle:300", "snapshot:Foreground"],
            "after the batch: the settle wait, then one foreground snapshot");
        rig.Snapshots.Should().Be(1, "one observation for the whole sequence, none per step");
        rig.Log.Should().NotContain("step-settle");
        text.Should().NotContain("\r");
        Json(text).GetProperty("completed").GetInt32().Should().Be(3, "the JSON result comes first, unchanged");
        text.Should().StartWith(Json(text).GetRawText() + "\n\n", "the observation is appended after a blank line");
        text.Should().EndWith(SnapshotRenderer.Render(rig.ForegroundSnap), "the E-5 observation is the rendered snapshot");
        text.Should().ContainEquivalentOf("foreground");
    }

    public static TheoryData<string> StopKinds => new() { "error", "wait_for", "assert_element", "takeover" };

    [Theory]
    [MemberData(nameof(StopKinds))]
    public async Task Observe_foreground_observes_a_batch_that_stopped_early(string stop)
    {
        var rig = new Rig();
        string middle = Key("b");
        switch (stop)
        {
            case "error":
                rig.Throw["key:b"] = new InvalidOperationException("refused");
                break;
            case "wait_for":
                rig.WaitResult = new WaitForResult(false, "element_exists", 0, 1, "not there");
                middle = """{"tool":"wait_for","args":{"text":"x","timeout_ms":0}}""";
                break;
            case "assert_element":
                rig.AssertOutcome = new AssertResult("el_5", "enabled", false, "disabled");
                middle = """{"tool":"assert_element","args":{"element_id":"el_5","state":"enabled"}}""";
                break;
            default:
                rig.After = entry => { if (entry == "key:a") rig.Pending = true; };
                break;
        }

        var text = await rig.Run(Steps(Key("a"), middle, Key("c")), "foreground", 100);

        Json(text).GetProperty("stoppedBy").GetString().Should().Be(stop);
        rig.InputSent.Should().NotContain("key:c");
        rig.Snapshots.Should().Be(1, "where the screen is now is exactly what the caller needs after a batch that stopped early");
        rig.Log.Should().Contain("settle:100");
        text.Should().EndWith(SnapshotRenderer.Render(rig.ForegroundSnap));
    }

    [Fact]
    public async Task Observe_none_takes_no_snapshot_and_returns_bare_json()
    {
        var rig = new Rig();

        var text = await rig.Run(Steps(Key("a")));

        rig.Snapshots.Should().Be(0);
        rig.Log.Should().NotContain(e => e.StartsWith("settle:", StringComparison.Ordinal));
        var parse = () => JsonDocument.Parse(text).Dispose();
        parse.Should().NotThrow("with observe:none the result is the JSON alone");
    }

    [Fact]
    public async Task A_failed_observation_is_a_note_after_the_result_not_an_error()
    {
        var rig = new Rig { SnapshotFailure = new InvalidOperationException("UIA went away") };

        var text = await rig.Run(Steps(Key("a")), "foreground", 0);

        Json(text).GetProperty("completed").GetInt32().Should().Be(1);
        text.Should().Contain("Observation failed").And.Contain("UIA went away");
    }

    // ---- GREEN close-out: the edges the implementation revealed ------------------------------------

    [Fact]
    public async Task The_result_has_exactly_the_documented_keys_and_each_entry_result_or_error()
    {
        var rig = new Rig();
        rig.Throw["key:b"] = new InvalidOperationException("refused");

        var root = Json(await rig.Run(Steps(Key("a"), Key("b"))));

        root.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["count", "completed", "results", "failedIndex", "error", "stoppedBy"], "the shape the design note documents, nothing else");
        var results = Results(root);
        results[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["index", "tool", "ok", "result"]);
        results[1].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["index", "tool", "ok", "error"],
            "a step that threw has error instead of result");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unsatisfied_wait_for_with_no_detail_still_stops_with_a_plain_error(string? detail)
    {
        var rig = new Rig { WaitResult = new WaitForResult(false, "element_exists", 0, 1, detail!) };

        var root = Json(await rig.Run(Steps("""{"tool":"wait_for","args":{"text":"x","timeout_ms":0}}""", Key("b"))));

        root.GetProperty("stoppedBy").GetString().Should().Be("wait_for");
        root.GetProperty("failedIndex").GetInt32().Should().Be(0);
        root.GetProperty("error").GetString().Should().Be("wait_for was not satisfied.", "no dangling ': ' when there is no detail to quote");
        Results(root)[0].GetProperty("ok").GetBoolean().Should().BeFalse();
        rig.InputSent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_wait_for_that_refuses_its_own_arguments_is_an_error_stop_not_an_unsatisfied_wait()
    {
        var rig = new Rig();

        var root = Json(await rig.Run(Steps(Key("a"), """{"tool":"wait_for","args":{"text":"x","timeout_ms":999999}}""", Key("c"))));

        root.GetProperty("stoppedBy").GetString().Should().Be("error", "the wait never ran; the tool refused it");
        root.GetProperty("failedIndex").GetInt32().Should().Be(1);
        root.GetProperty("error").GetString().Should().Contain("timeout_ms", "wait_for's own refusal, naming its argument");
        var failed = Results(root)[1];
        failed.GetProperty("ok").GetBoolean().Should().BeFalse();
        IsNull(failed, "result").Should().BeTrue();
        rig.Log.Should().NotContain(e => e.StartsWith("waitfor:", StringComparison.Ordinal));
        rig.InputSent.Should().Equal(["key:a"]);
    }

    [Fact]
    public async Task An_assert_element_that_throws_is_an_error_stop_not_a_failed_assertion()
    {
        var rig = new Rig();
        rig.Uia.Setup(s => s.AssertElementAsync("el_9", "exists", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Element el_9 is no longer available"));

        var root = Json(await rig.Run(Steps("""{"tool":"assert_element","args":{"element_id":"el_9","state":"exists"}}""", Key("b"))));

        root.GetProperty("stoppedBy").GetString().Should().Be("error");
        root.GetProperty("error").GetString().Should().Be("Element el_9 is no longer available");
        rig.InputSent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_satisfied_wait_for_and_a_passing_assert_are_ok_and_counted()
    {
        var rig = new Rig();

        var root = Json(await rig.Run(Steps(
            """{"tool":"wait_for","args":{"text":"File name","timeout_ms":0}}""",
            """{"tool":"assert_element","args":{"element_id":"el_5","state":"exists"}}""")));

        root.GetProperty("completed").GetInt32().Should().Be(2);
        Results(root).Should().OnlyContain(r => r.GetProperty("ok").GetBoolean());
        IsNull(root, "error").Should().BeTrue();
    }

    // ---- the two pure helpers, past what any real tool returns today --------------------------------
    // Every allowlisted tool returns a serialized DTO or fixed text, so no step can hand these an
    // invalid "{..." or a wait_for result without Satisfied, so the helpers are called directly
    // (internal static).

    private static object EmbedOf(string result) => SequenceTools.Embed(result);

    private static string? StopOf(string tool, string result) => SequenceTools.StopReason(tool, result, SequenceTools.Embed(result));

    [Theory]
    [InlineData("{not json")]           // starts like an object, is not one: the string, not a fault
    [InlineData("[1,")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PASS")]
    [InlineData("123")]                 // valid JSON, but neither an object nor an array
    [InlineData("\"quoted\"")]
    [InlineData("pressed {")]
    public void A_result_that_is_not_a_json_object_or_array_is_embedded_as_the_string(string result)
    {
        EmbedOf(result).Should().BeOfType<string>().Which.Should().Be(result);
    }

    [Theory]
    [InlineData("{\"a\":1}", JsonValueKind.Object)]
    [InlineData("  \r\n{\"a\":1}", JsonValueKind.Object)]
    [InlineData("[1,2]", JsonValueKind.Array)]
    public void A_json_object_or_array_result_is_embedded_as_json(string result, JsonValueKind kind)
    {
        EmbedOf(result).Should().BeOfType<JsonElement>().Which.ValueKind.Should().Be(kind);
    }

    [Theory]
    [InlineData("{\"Satisfied\":true,\"Detail\":\"x\"}", null)]
    [InlineData("{\"Satisfied\":false,\"Detail\":\"gone\"}", "wait_for was not satisfied: gone")]
    [InlineData("{\"Satisfied\":\"true\"}", "wait_for was not satisfied.")]     // only a JSON true is satisfied
    [InlineData("{\"Satisfied\":1}", "wait_for was not satisfied.")]
    [InlineData("{\"satisfied\":true}", "wait_for was not satisfied.")]         // the DTO's own casing, ordinal
    [InlineData("{\"Detail\":\"no flag\"}", "wait_for was not satisfied: no flag")]
    [InlineData("{\"Satisfied\":false,\"Detail\":5}", "wait_for was not satisfied.")]
    [InlineData("[true]", "wait_for was not satisfied.")]
    [InlineData("{not json", "wait_for was not satisfied.")]
    [InlineData("", "wait_for was not satisfied.")]
    public void Only_a_wait_for_result_that_says_Satisfied_true_lets_the_batch_go_on(string result, string? stop)
    {
        StopOf("wait_for", result).Should().Be(stop);
    }

    [Theory]
    [InlineData("assert_element", "PASS", null)]
    [InlineData("assert_element", "PASS ", "assert_element did not pass: PASS ")]   // exact, as the tool returns it
    [InlineData("assert_element", "pass", "assert_element did not pass: pass")]
    [InlineData("assert_element", "", "assert_element did not pass: ")]
    [InlineData("key", "FAIL: anything", null)]                                       // only the two judging tools judge
    [InlineData("click", "{\"Satisfied\":false}", null)]
    public void Only_wait_for_and_assert_element_judge_their_result(string tool, string result, string? stop)
    {
        StopOf(tool, result).Should().Be(stop);
    }

    [Theory]
    [InlineData("""{"tool":"type","args":{"text":"hello"}}""")]
    [InlineData("""{"tool":"shortcut","args":{"shortcut":"ctrl+s"}}""")]
    [InlineData("""{"tool":"scroll","args":{"direction":"down","x":5,"y":6}}""")]
    [InlineData("""{"tool":"drag","args":{"from_x":1,"from_y":2,"to_x":3,"to_y":4,"duration_ms":0}}""")]
    [InlineData("""{"tool":"interact_element","args":{"element_id":"el_5","action":"invoke"}}""")]
    [InlineData("""{"tool":"wait_for","args":{"text":"x","timeout_ms":0}}""")]
    [InlineData("""{"tool":"assert_element","args":{"element_id":"el_5","state":"exists"}}""")]
    public async Task A_step_after_the_caller_cancelled_never_reaches_its_service(string next)
    {
        // E-6 round 2 (F1, the family): "the sequence's token is checked before every step ... once
        // the caller has cancelled, no further step starts - whether or not that step's tool takes a
        // token". Cancelled during step 0; step 1 is each kind of step in turn.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        rig.After = entry => { if (entry == "key:a") cts.Cancel(); };

        var act = () => rig.Run(Steps(Key("a"), next), ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the call ends with OperationCanceledException");
        int at = rig.Log.IndexOf("key:a");
        rig.Log.Skip(at + 1).Where(e => !IsIndicatorRead(e)).Should().BeEmpty(
            "step 1 never starts once the caller has cancelled: no service call at all, not even a target lookup");
    }

    [Fact]
    public async Task A_call_cancelled_before_it_starts_is_not_observed()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => rig.Run(Steps("""{"tool":"assert_element","args":{"element_id":"el_5","state":"exists"}}"""), "foreground", 100, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Uia.Invocations.Should().BeEmpty("no step ran and nothing was observed");
        rig.Log.Should().NotContain(e => e.StartsWith("settle:", StringComparison.Ordinal), "no settle wait for a cancelled call");
        rig.Snapshots.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cancelled_call_runs_no_further_step_even_one_that_takes_no_token(bool beforeStep0)
    {
        // "Cancellation is not a step failure: it propagates and the call ends." drag,
        // interact_element, wait_for and assert_element take no token of their own (each passes
        // CancellationToken.None or none at all), so only the dispatcher can end the call: a client
        // that cancelled must not have up to nineteen more steps of input sent - or twenty
        // wait_for steps of up to 120 s each run - after it gave up.
        var rig = new Rig();
        using var cts = new CancellationTokenSource();
        if (beforeStep0) cts.Cancel();
        else rig.After = entry => { if (entry == "key:a") cts.Cancel(); };
        var steps = Steps(
            beforeStep0 ? """{"tool":"drag","args":{"from_x":1,"from_y":2,"to_x":3,"to_y":4,"duration_ms":0}}""" : Key("a"),
            """{"tool":"drag","args":{"from_x":1,"from_y":2,"to_x":3,"to_y":4,"duration_ms":0}}""",
            """{"tool":"interact_element","args":{"element_id":"el_5","action":"invoke"}}""",
            """{"tool":"assert_element","args":{"element_id":"el_5","state":"exists"}}""",
            """{"tool":"wait_for","args":{"text":"x","timeout_ms":0}}""");

        var act = () => rig.Run(steps, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the cancelled call ends instead of returning a result"); rig.InputSent.Should().Equal(beforeStep0 ? Array.Empty<string>() : ["key:a"], "no step starts after the call was cancelled");
        rig.Log.Should().NotContain(e => e.StartsWith("assert:", StringComparison.Ordinal) || e.StartsWith("waitfor:", StringComparison.Ordinal));
    }
}

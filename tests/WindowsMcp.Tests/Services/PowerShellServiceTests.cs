using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class PowerShellServiceTests
{
    [Fact]
    public async Task RunAsync_executes_simple_echo_and_captures_stdout()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("'hello from PS'");
        result.Success.Should().BeTrue();
        result.Stdout.Trim().Should().Be("hello from PS");

        // F1: the per-stream bound is not a tax on ordinary output — a result that fits reports
        // that nothing was dropped, so a caller can trust Stdout to be the whole answer.
        result.StdoutTrimmedChars.Should().Be(0);
        result.StderrTrimmedChars.Should().Be(0);
    }

    // REGRESSION: `powershell -Command -` with the script piped to stdin evaluates input
    // LINE BY LINE as separate statements, so any multi-line construct is silently mangled and
    // the process still exits 0 with EMPTY stdout. This made disk_inspect mode:reclaimable
    // return nothing on exit 0. The script must be parsed as a single unit.
    [Fact]
    public async Task RunAsync_multiline_hashtable_literal_produces_output()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var script = "[PSCustomObject]@{\n    Alpha = 1\n    Beta  = 2\n} | ConvertTo-Json";
        var result = await svc.RunAsync(script);
        result.Stdout.Should().NotBeNullOrWhiteSpace("a multi-line script must not silently produce nothing");
        result.Stdout.Should().Contain("Alpha").And.Contain("Beta");
    }

    [Fact]
    public async Task RunAsync_multiline_try_catch_executes_as_one_unit()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var script = "try {\n    $v = 6 * 7\n    Write-Output $v\n} catch {\n    Write-Output 'failed'\n}";
        var result = await svc.RunAsync(script);
        result.Stdout.Trim().Should().Be("42");
    }

    [Fact]
    public async Task RunAsync_multiline_foreach_accumulates_across_lines()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var script = "$total = 0\nforeach ($i in 1..4) {\n    $total += $i\n}\nWrite-Output $total";
        var result = await svc.RunAsync(script);
        result.Stdout.Trim().Should().Be("10");
    }

    // Guards the temp-file fallback: stdin had no length limit, but a command line does
    // (~32767 chars), so a large script must still run rather than regress.
    [Fact]
    public async Task RunAsync_very_large_script_still_executes()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var padding = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"# filler comment line {i} ----------"));
        var script = padding + "\n[PSCustomObject]@{\n    Big = 'yes'\n} | ConvertTo-Json";
        script.Length.Should().BeGreaterThan(12_000, "the test must actually exceed the EncodedCommand budget");
        var result = await svc.RunAsync(script);
        result.Stdout.Should().Contain("Big");
    }

    // UTF-16LE encoding correctness: non-ASCII must survive the round trip.
    [Fact]
    public async Task RunAsync_preserves_non_ascii_characters()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Write-Output 'em—dash café ✓'");
        result.Stdout.Should().Contain("em—dash").And.Contain("café").And.Contain("✓");
    }

    [Fact]
    public async Task RunAsync_returns_error_for_invalid_command()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Get-DoesNotExistCommand");
        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    // REGRESSION: PS 5.1 with redirected stderr wraps ALL non-stdout streams in CLIXML, so
    // benign progress records (e.g. "Preparing modules for first use." on first-touch module
    // import) land on stderr and used to flip Success=false with phantom "errors" on perfectly
    // good commands. Only genuine <S S="Error"> records may count against Success.
    // D-8 flipped this test's precondition: progress used to reach Stderr as ~600 characters of
    // CLIXML on every call. The preamble now suppresses it at the source. The CLIXML decoding this
    // test used to exercise incidentally is covered properly by ClixmlStderrTests.
    [Fact]
    public async Task RunAsync_progress_output_is_suppressed()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Write-Progress -Activity 'probe' -Status 'working'; 'clean'");

        result.Stderr.Should().BeEmpty("progress output has no console to draw on and must not reach the model");
        result.Success.Should().BeTrue("a progress record is not an error");
        result.Errors.Should().BeEmpty();
        result.Stdout.Trim().Should().Be("clean");
    }

    // Layer 2 on its own: even when a script re-enables progress, the records are dropped in the
    // decoder rather than shipped as XML.
    [Fact]
    public async Task RunAsync_progress_re_enabled_by_the_script_is_still_dropped()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync(
            "$ProgressPreference='Continue'; Write-Progress -Activity 'probe' -Status 'working'; 'clean'");

        result.Stderr.Should().NotContain("<Objs").And.NotContain("progress");
        result.Success.Should().BeTrue();
        result.Stdout.Trim().Should().Be("clean");
    }

    [Fact]
    public async Task RunAsync_warning_records_on_stderr_do_not_fail_the_command()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Write-Warning 'careful'; 'warned'");

        result.Success.Should().BeTrue("a warning is not an error");
        result.Errors.Should().BeEmpty();
        result.Stdout.Trim().Should().Be("warned");

        // D-8: the warning survives as readable text, not as the CLIXML the host emits.
        result.Stderr.Should().Contain("careful").And.NotContain("<Objs");
    }

    [Fact]
    public async Task RunAsync_real_error_records_still_fail_and_are_extracted_as_text()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Write-Error 'boom'; 'after'");

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("boom"));
        result.Errors.Should().OnlyContain(e => !e.StartsWith("<"), "errors must be decoded text, not raw CLIXML");
        result.Stdout.Trim().Should().Be("after", "a non-terminating error must not eat stdout");
    }

    [Fact]
    public async Task RunAsync_serialized_calls_preserve_per_caller_output()
    {
        // Fire N calls concurrently; the service's gate serializes them and each caller must get
        // back exactly its own output. The property (serialization + no cross-caller contamination)
        // is independent of N — N is only a stress knob. Kept modest on purpose: every call spawns
        // a fresh powershell.exe, and a Defender-scanned cold-start is ~15-18 s here, so a large N
        // measures antivirus scan time, not the serialization logic (and previously blew the
        // per-call backstop for queued callers — since fixed by starting the backstop after the
        // gate is acquired rather than before).
        const int N = 12;
        using var svc = new PowerShellService(NullLogger.Instance);
        var tasks = Enumerable.Range(0, N).Select(i =>
            svc.RunAsync($"'{i}'")).ToArray();
        var results = await Task.WhenAll(tasks);
        for (int i = 0; i < N; i++)
            results[i].Stdout.Trim().Should().Be(i.ToString());
    }

    [Fact]
    public async Task RunAsync_dispose_throws_object_disposed_exception()
    {
        var svc = new PowerShellService(NullLogger.Instance);
        svc.Dispose();
        Func<Task> act = () => svc.RunAsync("'never reached'");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    /// <summary>
    /// C-6: the backstop is folded into the SAME result path as the per-call timeout, for the
    /// overload that takes a clock. It used to throw <c>OperationCanceledException</c>, which threw
    /// away the stdout the script had already written — usually the diagnosis. Only the CALLER's
    /// cancellation throws now.
    /// <para>
    /// F8: the caller that asked for a clock (<c>timeout: null</c> is still asking) reads
    /// <c>TimedOut</c>; the one-argument overload every internal service uses gets the loud
    /// <c>TimeoutException</c> instead — see the sibling below.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_backstop_timeout_tears_down_a_runaway_script()
    {
        // Short backstop; a 30s sleep would hang the gate forever without the timeout.
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await svc.RunAsync("Start-Sleep -Seconds 30", null);

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        result.TimedOut.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.Errors.Should().ContainSingle().Which
            .Should().StartWith("timed out after").And.Contain("(execution backstop)",
                "the two clocks behave alike, and the result says which one fired");
    }

    // ---- C-6: the per-call timeout -----------------------------------------------------------

    /// <summary>
    /// C-6 (roadmap R9): expiry RETURNS. The child tree is killed, the stdout written before the
    /// script hung is kept, and the result says <c>TimedOut</c> — the diagnosis survives.
    /// </summary>
    [Fact]
    public async Task RunAsync_timeout_returns_the_partial_output_and_kills_the_child()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // $PID first so the test can prove the child is gone; 'before' so it can prove the
        // partial stdout was harvested rather than discarded with the kill.
        var result = await svc.RunAsync("$PID; 'before'; Start-Sleep -Seconds 30", TimeSpan.FromSeconds(2));

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
            "the 2-second budget bounds the call; the 30-second sleep must not run to the end");
        result.TimedOut.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.ExitCode.Should().Be(-1);
        result.Errors.Should().Equal("timed out after 2s");
        result.Stdout.Should().Contain("before", "the output written before the hang is the diagnosis");

        var childPid = int.Parse(result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim());
        Action lookUp = () => System.Diagnostics.Process.GetProcessById(childPid);
        lookUp.Should().Throw<ArgumentException>(
            "the child tree is killed on expiry - an orphaned powershell.exe would hold the pipe and the CPU");
    }

    /// <summary>C-6: the seconds are reported as given, invariant, to at most three decimals.</summary>
    [Fact]
    public async Task RunAsync_timeout_reports_fractional_seconds_as_written()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        var result = await svc.RunAsync("Start-Sleep -Seconds 30", TimeSpan.FromMilliseconds(1500));

        result.TimedOut.Should().BeTrue();
        result.Errors.Should().Equal("timed out after 1.5s");
    }

    [Fact]
    public async Task RunAsync_with_a_null_timeout_still_runs_to_completion()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        var result = await svc.RunAsync("'done'", null);

        result.TimedOut.Should().BeFalse("null means the execution backstop only - today's behaviour");
        result.Success.Should().BeTrue();
        result.Stdout.Trim().Should().Be("done");
    }

    [Fact]
    public async Task RunAsync_with_a_generous_timeout_completes_and_reports_no_timeout()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        var result = await svc.RunAsync("'quick'", TimeSpan.FromSeconds(300));

        result.TimedOut.Should().BeFalse();
        result.Success.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().Be("quick");
    }

    /// <summary>
    /// C-6: the timer starts AFTER the serialization gate is acquired (the backstop's rule in
    /// CLAUDE.md). A caller queued behind somebody else's script must not burn its own budget
    /// waiting — it never got to run.
    /// </summary>
    [Fact]
    public async Task RunAsync_timeout_clock_starts_after_the_gate_not_when_queued()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        // A holds the gate for ~5 s of execution plus a cold start; B is queued immediately behind
        // it with a 2-second budget and must still succeed.
        var slow = svc.RunAsync("Start-Sleep -Seconds 5", null);
        var queued = svc.RunAsync("'ok'", TimeSpan.FromSeconds(2));

        await Task.WhenAll(slow, queued);

        (await queued).TimedOut.Should().BeFalse(
            "the 2-second budget bounds B's execution, not the time it spent waiting for A");
        (await queued).Stdout.Trim().Should().Be("ok");
        (await slow).TimedOut.Should().BeFalse();
    }

    /// <summary>
    /// C-6: a timeout longer than the backstop is not an error — the earlier clock fires and the
    /// result says which one it was.
    /// </summary>
    [Fact]
    public async Task RunAsync_timeout_longer_than_the_backstop_lets_the_backstop_win()
    {
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromMilliseconds(500));

        var result = await svc.RunAsync("Start-Sleep -Seconds 30", TimeSpan.FromSeconds(600));

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        result.Errors.Should().ContainSingle().Which.Should().Contain("(execution backstop)",
            "the backstop fired first, and the caller is told which clock ended the call");
    }

    /// <summary>
    /// C-6: the child's environment is the (repaired) process block, inherited — there is no
    /// per-spawn rebuild. <c>EnvironmentRepair</c> is the one place the block is fixed, so jobs,
    /// <c>start_process</c> and <c>launch</c> get the same Path this does.
    /// </summary>
    [Fact]
    public void CreateStartInfo_inherits_the_process_environment_and_builds_none_of_its_own()
    {
        var startInfo = PowerShellInvocation.CreateStartInfo("-NoProfile -Command \"'x'\"");

        startInfo.UseShellExecute.Should().BeFalse("an inherited block requires UseShellExecute:false");
        startInfo.Environment["Path"].Should().Be(Environment.GetEnvironmentVariable("Path"),
            "the child gets exactly the Path this process has - repaired once, at startup");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    public async Task RunAsync_non_positive_timeout_is_refused_without_spawning_anything(int seconds)
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.RunAsync("'never reached'", TimeSpan.FromSeconds(seconds));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*timeout*");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "a refusal is decided before the gate and before any powershell.exe cold start");
    }

    /// <summary>
    /// C-6 + D-8: the timeout path runs the CLIXML decoder, exactly as the completion path does.
    /// <para>
    /// Measured 2026-09-08: Windows PowerShell 5.1 writes the <c>#&lt; CLIXML</c> header to stderr
    /// as soon as the first non-stdout record is produced, but buffers the <c>&lt;Objs&gt;</c>
    /// records themselves until the host shuts down. So a killed child leaves <b>only the header</b>
    /// on the pipe — the warning itself does not survive the kill, while stdout, which is flushed
    /// per statement, does. A timeout path that returned the raw pump would therefore hand the
    /// model <c>"#&lt; CLIXML"</c> and no information at all; decoded, the header is correctly
    /// nothing. <c>'reached'</c> on stdout is what proves the warning statement ran and the header
    /// was written, so this cannot pass vacuously by killing the child before it wrote anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_timeout_decodes_the_stderr_it_harvested_instead_of_returning_the_clixml_header()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        var result = await svc.RunAsync(
            "'reached'; Write-Warning 'careful'; Start-Sleep -Seconds 30", TimeSpan.FromSeconds(5));

        result.TimedOut.Should().BeTrue();
        result.Stdout.Should().Contain("reached",
            "the script got as far as the warning, so PowerShell did open its CLIXML stderr document");
        result.Stderr.Should().NotContain("CLIXML",
            "the raw header never reaches the model - the harvest is decoded like any other stderr");
        result.Stderr.Should().NotContain("<Objs", "nor the XML it wraps the records in");
        result.Errors.Should().ContainSingle("a warning is not an error; the only error is the clock that ended the call")
            .Which.Should().Be("timed out after 5s");
    }

    [Fact]
    public async Task RunAsync_honors_caller_cancellation_token()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.RunAsync("Start-Sleep -Seconds 30", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    // ---- F1: the foreground result is bounded exactly like a job's ----------------------------

    /// <summary>
    /// F1: the per-stream capacity, the same one <c>JobService</c> gives a background job's
    /// buffers. A foreground call had none: a chatty script grew a <c>StringBuilder</c> until the
    /// clock stopped it, and the whole of it was then copied into one string and serialized to the
    /// client.
    /// </summary>
    private const int StreamCapacityChars = 1_000_000;

    /// <summary>
    /// F1: a script that floods stdout and then never exits. The clock ends the call either way;
    /// what is under test is that the answer is bounded — a runaway <c>while($true)</c> must not
    /// hand back everything it managed to write in three seconds.
    /// </summary>
    [Fact]
    public async Task RunAsync_bounds_the_stdout_of_a_script_that_floods_and_then_hangs()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = await svc.RunAsync("$s='x'*1000; while($true){$s}", TimeSpan.FromSeconds(3));

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the clock still bounds the call");
        result.TimedOut.Should().BeTrue();
        result.Stdout.Length.Should().BeLessThanOrEqualTo(StreamCapacityChars,
            "a foreground call is bounded like a job: the buffer keeps the tail and drops the rest");
        result.StdoutTrimmedChars.Should().BeGreaterThan(0,
            "the caller has to know output was dropped - a silently short answer reads as the whole one");
    }

    /// <summary>
    /// F1: the same bound on a script that EXITS. Nothing here is a timeout — a successful command
    /// that prints 1.6 M characters is capped at the capacity, keeps the most recent tail (line
    /// 1600, not line 1) and says how much went.
    /// </summary>
    [Fact]
    public async Task RunAsync_keeps_the_tail_when_a_successful_script_outruns_the_buffer()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        var result = await svc.RunAsync(@"1..1600 | % { ""$_"" + 'x'*1000 }");

        result.Success.Should().BeTrue("the script finished; being too chatty is not a failure");
        result.TimedOut.Should().BeFalse();
        result.Stdout.Length.Should().Be(StreamCapacityChars, "~1.6 M characters were written into a 1 M buffer");
        result.StdoutTrimmedChars.Should().BeGreaterThan(0);
        result.Stdout.Should().Contain("1600x", "the TAIL is what is kept - the newest output is the useful end");
        result.StderrTrimmedChars.Should().Be(0, "nothing was written to stderr, so nothing was dropped from it");
    }

    // ---- F6: what the script said before the clock fired ---------------------------------------

    /// <summary>
    /// F6: the timeout path used to replace <c>Errors</c> with the clock's reason alone, so the
    /// error the script wrote — the actual diagnosis — reached the model only as raw stderr it had
    /// to notice for itself. The extracted errors come first and the reason last, so the reason for
    /// the call ending stays at the end where a reader looks for it.
    /// </summary>
    [Fact]
    public async Task RunAsync_timeout_keeps_the_errors_the_script_wrote_before_the_clock_fired()
    {
        using var svc = new PowerShellService(NullLogger.Instance);

        var result = await svc.RunAsync(
            "cmd /c \"echo DISK FAILURE 1>&2\"; 'still alive'; Start-Sleep -Seconds 60", TimeSpan.FromSeconds(5));

        result.TimedOut.Should().BeTrue();
        result.Stdout.Should().Contain("still alive", "the script did get past the stderr write");
        result.Stderr.Should().Contain("DISK FAILURE", "the harvested stderr is still reported whole");
        result.Errors.Should().Equal(new[] { "DISK FAILURE", "timed out after 5s" },
            "what the script reported comes first and why the call ended comes last; a clock that "
            + "erases the diagnosis leaves the caller with nothing but 'it timed out'");
    }

    // ---- F7: which clock ended the call --------------------------------------------------------

    /// <summary>
    /// F7: <c>timeout == backstop</c> is the CALLER's clock, not the server's. The caller asked for
    /// exactly that many seconds and got exactly that many; blaming the "execution backstop" tells
    /// it to shorten a timeout it chose deliberately, and hides that its own budget is what ran out.
    /// </summary>
    [Fact]
    public async Task RunAsync_a_timeout_equal_to_the_backstop_is_reported_as_the_callers_clock()
    {
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromSeconds(4));

        var result = await svc.RunAsync("Start-Sleep -Seconds 30", TimeSpan.FromSeconds(4));

        result.TimedOut.Should().BeTrue();
        result.Errors.Should().Equal(new[] { "timed out after 4s" },
            "the caller's own 4 seconds ran out; the backstop happens to agree and is not the news");
        result.Errors[0].Should().NotContain("(execution backstop)");
    }

    /// <summary>F7: one millisecond over, and the server's clock is genuinely the earlier one.</summary>
    [Fact]
    public async Task RunAsync_a_timeout_one_millisecond_past_the_backstop_is_the_backstops()
    {
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromSeconds(4));

        var result = await svc.RunAsync("Start-Sleep -Seconds 30", TimeSpan.FromMilliseconds(4001));

        result.TimedOut.Should().BeTrue();
        result.Errors.Should().ContainSingle().Which.Should().Contain("(execution backstop)",
            "the caller asked for longer than the server allows, so the server's clock is the one "
            + "that ended the call and the caller is told so");
    }

    // ---- F8: the overload without a clock is loud ----------------------------------------------

    /// <summary>
    /// F8: every internal caller (disk, storage, security, firewall, network, audio, file streams,
    /// notification …) uses the one-argument overload, which asks for no clock at all. Folding the
    /// backstop into the RESULT for them turned a 15-minute runaway into <c>Success:false</c> with
    /// an empty <c>Stdout</c> — indistinguishable from "the query found nothing", which is exactly
    /// the failure mode <c>disk_inspect mode:reclaimable</c> shipped. A <c>TimeoutException</c> is
    /// caller-facing (<c>ToolErrors.IsCallerFacing</c>), so the reason reaches the model unmasked.
    /// </summary>
    [Fact]
    public async Task RunAsync_without_a_timeout_argument_throws_when_the_backstop_fires()
    {
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.RunAsync("Start-Sleep -Seconds 30");

        var thrown = await act.Should().ThrowAsync<TimeoutException>(
            "a caller that never asked for a clock cannot read TimedOut; silence is how a backstop "
            + "becomes an empty answer nobody questions");
        thrown.WithMessage("*execution backstop*", "the message is the reason, verbatim");
        thrown.WithMessage("*timed out after 0.5s*", "including how long it was given");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// F8, the other half: throwing is only useful if the message survives the SDK. The CallTool
    /// filter (<c>WindowsMcpHost.AddWindowsMcp</c>) asks <c>ToolErrors.IsCallerFacing</c> and
    /// sends <c>ToolErrors.MessageFor</c>; anything it does not claim is masked as "An error
    /// occurred invoking '&lt;tool&gt;'." — which is exactly the silence F8 exists to end. The type
    /// is pinned in <c>ToolErrorsTests</c>; this pins the exception the service really throws.
    /// </summary>
    [Fact]
    public async Task RunAsync_backstop_timeout_reaches_the_client_through_the_tool_error_filter()
    {
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromMilliseconds(500));

        var thrown = await Record.ExceptionAsync(() => svc.RunAsync("Start-Sleep -Seconds 30"));

        thrown.Should().BeOfType<TimeoutException>();
        ToolErrors.IsCallerFacing(thrown!).Should().BeTrue(
            "a masked backstop is indistinguishable from a crash, and the caller retries it");
        ToolErrors.MessageFor(thrown!).Should().Be("timed out after 0.5s (execution backstop)",
            "the message IS the answer: how long it ran and whose clock ended it, word for word");
    }

    // ---- F14: the clock cannot cut the temp-script write ----------------------------------------

    /// <summary>Every <c>winmcp-*.ps1</c> the temp directory holds right now.</summary>
    /// <remarks>
    /// Only <see cref="PowerShellServiceTests"/> runs scripts long enough to need the temp-file
    /// fallback, and xUnit runs the tests of one class one at a time — so a file that appears
    /// across a call in this class was written by that call.
    /// </remarks>
    private static HashSet<string> TempScripts()
        => Directory.EnumerateFiles(Path.GetTempPath(), "winmcp-*.ps1")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Files this test's call left behind, removed before the assertion so a RED run does not leave
    /// megabytes in <c>%TEMP%</c>.
    /// </summary>
    private static string[] TakeLeftovers(HashSet<string> before)
    {
        var leftovers = TempScripts().Except(before).ToArray();
        foreach (var file in leftovers)
        {
            try { File.Delete(file); } catch { /* best effort: the assertion is the point */ }
        }
        return leftovers;
    }

    /// <summary>
    /// F14: a script too long for <c>-EncodedCommand</c> is written to a temp <c>.ps1</c> first.
    /// The ordinary case: the write finishes, the script hangs, and the clock ends the call the
    /// same way it ends a small one — a result, not an exception — with the file cleaned up.
    /// </summary>
    [Fact]
    public async Task RunAsync_of_an_oversized_script_that_hangs_returns_a_timeout_and_leaves_no_temp_script()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var padding = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"# filler comment line {i} ----------"));
        var script = padding + "\nStart-Sleep -Seconds 60";
        script.Length.Should().BeGreaterThan(12_000, "the test must actually take the -File path");
        var before = TempScripts();

        string[] leftovers;
        PSResult result;
        try { result = await svc.RunAsync(script, TimeSpan.FromSeconds(2)); }
        finally { leftovers = TakeLeftovers(before); }

        leftovers.Should().BeEmpty("the temp script is deleted whichever way the call ended");
        result.TimedOut.Should().BeTrue();
        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// F14: the write is the caller's to cancel and nobody else's. With a clock shorter than the
    /// write itself, the call must still come back as a <c>TimedOut</c> RESULT: a clock that cuts
    /// the build instead throws <c>OperationCanceledException</c> out of a call that asked for a
    /// timeout, which the SDK masks as "An error occurred invoking 'powershell'." — and leaves the
    /// half-written script behind, because the path to delete is only recorded once the write
    /// returns.
    /// <para>
    /// 4 MB is chosen so encoding and writing it takes tens of milliseconds — far longer than the
    /// 5 ms clock — and the tail is <c>'done'</c> rather than a sleep so the child cannot outlive
    /// the test even if the kill loses a race.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RunAsync_with_a_clock_shorter_than_the_temp_script_write_still_returns_a_timeout()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var script = new string('#', 4_000_000) + "\n'done'";
        var before = TempScripts();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // The cleanup runs even when the call throws — which is what it does today, and a 4 MB
        // half-written script is not something a failing test should leave in %TEMP%.
        string[] leftovers;
        PSResult result;
        try { result = await svc.RunAsync(script, TimeSpan.FromMilliseconds(5)); }
        finally { leftovers = TakeLeftovers(before); }

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        result.TimedOut.Should().BeTrue("the clock ended the call, and a clock ending a call is a result");
        result.Errors.Should().Contain(e => e.Contains("timed out"));
        leftovers.Should().BeEmpty(
            "a build the clock cut leaves a partial script in %TEMP% that nothing ever deletes");
    }

    /// <summary>
    /// F14: the build under an already-cancelled token — the caller's own cancellation, the one
    /// case that legitimately stops it — writes nothing and leaves nothing.
    /// </summary>
    [Fact]
    public async Task BuildArgumentsAsync_cancelled_before_the_write_leaves_no_temp_script()
    {
        var padding = string.Join("\n", Enumerable.Range(0, 1500).Select(i => $"# filler comment line {i} ----------"));
        padding.Length.Should().BeGreaterThan(12_000, "the test must actually take the -File path");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var before = TempScripts();

        Func<Task> act = () => PowerShellInvocation.BuildArgumentsAsync(padding, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        TakeLeftovers(before).Should().BeEmpty(
            "a cancelled build leaves no half-written script behind: nothing else knows its name");
    }
}

internal sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

using FluentAssertions;
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
    [Fact]
    public async Task RunAsync_progress_records_on_stderr_do_not_fail_the_command()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Write-Progress -Activity 'probe' -Status 'working'; 'clean'");

        result.Stderr.Should().Contain("progress", "the test must actually exercise the CLIXML path");
        result.Success.Should().BeTrue("a progress record is not an error");
        result.Errors.Should().BeEmpty();
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

    [Fact]
    public async Task RunAsync_backstop_timeout_tears_down_a_runaway_script()
    {
        // Short backstop; a 30s sleep would hang the gate forever without the timeout.
        using var svc = new PowerShellService(NullLogger.Instance, TimeSpan.FromMilliseconds(500));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Func<Task> act = () => svc.RunAsync("Start-Sleep -Seconds 30");

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
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
}

internal sealed class NullLogger : Microsoft.Extensions.Logging.ILogger
{
    public static readonly NullLogger Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}

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

    [Fact]
    public async Task RunAsync_returns_error_for_invalid_command()
    {
        using var svc = new PowerShellService(NullLogger.Instance);
        var result = await svc.RunAsync("Get-DoesNotExistCommand");
        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
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

using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-6 (roadmap R9), requirement R3: the two pieces of the timeout path that a real
/// <c>powershell.exe</c> cannot be made to demonstrate on demand.
/// <para>
/// A grandchild that outlives the tree kill and keeps the pipe open is the case
/// <see cref="PowerShellService.HarvestAsync"/> exists for, and it cannot be provoked from a test:
/// a process inside the tree is killed with it, and one started outside it never inherits the
/// handle. Likewise <see cref="PowerShellService.Pump"/>'s whole reason to exist — a buffer that
/// can be read <b>before</b> the read has finished, which <c>ReadToEndAsync</c> cannot give — is
/// only observable while a writer is deliberately held open. Both are pinned here on hand-made
/// tasks and an in-memory pipe; <see cref="PowerShellServiceTests"/> covers the same code through
/// a real child.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PowerShellServiceHarvestTests
{
    // ---- HarvestAsync -----------------------------------------------------------------------

    /// <summary>
    /// The grace is a constant of the design, not a knob: long enough that a pipe closing as the
    /// tree dies is always drained, short enough that a survivor cannot hold the serialization
    /// gate. A change here changes how long every timed-out call blocks.
    /// </summary>
    [Fact]
    public void HarvestGrace_is_two_seconds()
    {
        PowerShellService.HarvestGrace.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task HarvestAsync_waits_for_both_pumps_and_returns_as_soon_as_they_finish()
    {
        var stdout = new TaskCompletionSource();
        var stderr = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();

        var harvest = PowerShellService.HarvestAsync(
            stdout.Task, stderr.Task, TimeSpan.FromSeconds(30), CancellationToken.None);

        harvest.IsCompleted.Should().BeFalse("neither pump has drained yet");

        stdout.SetResult();
        await Task.Delay(50);
        harvest.IsCompleted.Should().BeFalse(
            "stderr is still draining - the harvest waits for BOTH pipes, not the first one to close");

        stderr.SetResult();

        await harvest.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "both pumps finished, so the harvest returns then - it does not sit out the 30-second grace");
    }

    /// <summary>
    /// The branch that matters: a pipe nobody will ever close. The grace expires, the harvest
    /// RETURNS (the caller goes on to report whatever the pumps buffered), and the gate is
    /// released instead of being held by the survivor.
    /// </summary>
    [Theory]
    [InlineData(true)]      // the stdout pipe is the one still held
    [InlineData(false)]     // the stderr pipe is the one still held
    public async Task HarvestAsync_returns_when_the_grace_expires_on_a_pump_that_never_finishes(bool stdoutHangs)
    {
        var never = new TaskCompletionSource().Task;
        var stdout = stdoutHangs ? never : Task.CompletedTask;
        var stderr = stdoutHangs ? Task.CompletedTask : never;
        var sw = Stopwatch.StartNew();

        Func<Task> act = () => PowerShellService.HarvestAsync(
            stdout, stderr, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a survivor holding the pipe is not a failure of the call - what was buffered is the harvest");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50),
            "the grace is waited out; the harvest does not give up the moment a pump is unfinished");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "and the 100 ms grace bounds it - the pump never finishes, so nothing else can end this wait");
    }

    /// <summary>
    /// A pump whose read threw (a broken pipe as the child dies is the usual cause): the bytes it
    /// already buffered are still the best diagnosis available, so the fault must not escape and
    /// turn a reportable timeout into an invocation error.
    /// </summary>
    [Theory]
    [InlineData(true)]      // stdout's read faulted
    [InlineData(false)]     // stderr's read faulted
    public async Task HarvestAsync_returns_when_a_pump_faulted(bool stdoutFaulted)
    {
        var faulted = Task.FromException(new IOException("the pipe has been ended"));
        var stdout = stdoutFaulted ? faulted : Task.CompletedTask;
        var stderr = stdoutFaulted ? Task.CompletedTask : faulted;
        var sw = Stopwatch.StartNew();

        Func<Task> act = () => PowerShellService.HarvestAsync(
            stdout, stderr, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync("the buffer a faulted pump already filled is still the answer");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "a fault is known immediately - there is nothing left to wait for");
    }

    /// <summary>
    /// The <c>when (ct.IsCancellationRequested)</c> filter, from the other side: a pump cancelled
    /// by something that is not the caller's token is swallowed like any other pump failure. Only
    /// the CALLER's cancellation ends the call.
    /// </summary>
    [Fact]
    public async Task HarvestAsync_returns_when_a_pump_was_cancelled_and_the_caller_was_not()
    {
        using var pumpCts = new CancellationTokenSource();
        await pumpCts.CancelAsync();
        var cancelledPump = Task.FromCanceled(pumpCts.Token);
        var sw = Stopwatch.StartNew();

        Func<Task> act = () => PowerShellService.HarvestAsync(
            Task.CompletedTask, cancelledPump, TimeSpan.FromSeconds(30), CancellationToken.None);

        await act.Should().NotThrowAsync(
            "the pump's own cancellation is not the caller's - the caller still gets its timed-out result");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "a cancelled pump is finished: the harvest returns then, it does not sit out the grace");
    }

    [Fact]
    public async Task HarvestAsync_rethrows_the_callers_cancellation_when_the_token_is_already_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var never = new TaskCompletionSource().Task;
        var alsoNever = new TaskCompletionSource().Task;
        var sw = Stopwatch.StartNew();

        Func<Task> act = () => PowerShellService.HarvestAsync(
            never, alsoNever, TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's cancellation is never swallowed - the call is over, and a result must not be "
            + "returned as if the clock had merely run out");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "the cancellation ends the wait, not the 30-second grace");
    }

    [Fact]
    public async Task HarvestAsync_rethrows_the_callers_cancellation_when_it_arrives_during_the_grace()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var never = new TaskCompletionSource().Task;
        var alsoNever = new TaskCompletionSource().Task;
        var sw = Stopwatch.StartNew();

        Func<Task> act = () => PowerShellService.HarvestAsync(
            never, alsoNever, TimeSpan.FromSeconds(30), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a caller that walks away mid-harvest is answered with cancellation, not with a harvested result");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the token ended it, not the grace");
    }

    // ---- Pump -------------------------------------------------------------------------------

    /// <summary>
    /// The property the whole class exists for: the text read SO FAR, while the read is still
    /// running. <c>ReadToEndAsync</c> returns only at EOF, so on the timeout path — where the
    /// child is killed and its pipe may never cleanly close — it would return nothing at all.
    /// </summary>
    [Fact]
    public async Task Pump_exposes_what_it_has_read_while_the_writer_is_still_open()
    {
        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        var pump = new PowerShellService.Pump(reader, CancellationToken.None);

        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("before"));
        await WaitUntilAsync(() => pump.Text.Length > 0, TimeSpan.FromSeconds(1), "the chunk reaches the buffer");

        pump.Text.Should().Be("before",
            "the buffer is readable before the read has finished - this is what a ReadToEndAsync cannot give");
        pump.Completion.IsCompleted.Should().BeFalse(
            "the write end is still open, so the read has NOT finished - the point of the previous assertion");

        await pipe.Writer.CompleteAsync();

        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        pump.Completion.IsCompletedSuccessfully.Should().BeTrue("EOF ends the pump, it does not fault it");
        pump.Text.Should().Be("before", "closing the pipe ends the read; it does not change what was read");
    }

    [Fact]
    public async Task Pump_over_a_pipe_that_is_already_closed_completes_with_empty_text()
    {
        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);

        var pump = new PowerShellService.Pump(reader, CancellationToken.None);

        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        pump.Text.Should().BeEmpty(
            "a child that wrote nothing leaves an empty harvest - not a null, and not a hang");
    }

    /// <summary>
    /// The pump reads 4096 chars at a time and appends each chunk to one buffer, so a character
    /// whose UTF-8 bytes straddle two reads must survive. 5000 'é' is 10 000 bytes: past the chunk
    /// size, and split here at an ODD offset so the split lands between the two bytes of one 'é'.
    /// A pump that decoded each read independently would yield U+FFFD at the seam.
    /// </summary>
    [Fact]
    public async Task Pump_reassembles_multi_byte_text_split_across_a_chunk_boundary()
    {
        var text = new string('é', 5000);
        var bytes = Encoding.UTF8.GetBytes(text);
        bytes.Length.Should().Be(10_000, "two bytes per 'é' - the write has to be bigger than one 4096-char chunk");

        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        var pump = new PowerShellService.Pump(reader, CancellationToken.None);

        await pipe.Writer.WriteAsync(bytes.AsMemory(0, 5001));
        await WaitUntilAsync(() => pump.Text.Length > 0, TimeSpan.FromSeconds(1), "the first half is read");
        var half = pump.Text;
        half.Length.Should().BeGreaterThan(0).And.BeLessThan(5000,
            "the first write is consumed on its own, so the trailing half-character is genuinely held over");

        await pipe.Writer.WriteAsync(bytes.AsMemory(5001));
        await pipe.Writer.CompleteAsync();

        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        pump.Text.Length.Should().Be(5000, "no replacement character at the seam, nothing dropped, nothing doubled");
        pump.Text.Should().Be(text);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.Elapsed < limit)
            await Task.Delay(10);

        condition().Should().BeTrue($"{what} within {limit.TotalMilliseconds:0} ms");
    }
}

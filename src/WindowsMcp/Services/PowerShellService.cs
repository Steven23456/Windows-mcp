using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class PowerShellService : IPowerShellService
{
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _backstop;
    private bool _disposed;

    // Backstop so a runaway script (e.g. an accidental `while($true){}`) can't hold the
    // serialization gate forever and wedge every PowerShell-backed tool. Deliberately generous —
    // longer than any legitimate caller budget (storage_health caps its own CTS at 300s). The
    // normal cancellation path is the caller's CancellationToken; this is the last-resort teardown.
    private static readonly TimeSpan DefaultBackstop = TimeSpan.FromMinutes(15);

    // C-6: after a timeout kills the tree the pipes close as the children die and the pumps drain
    // what was written before the kill. A grandchild that survived the tree kill can keep a pipe
    // open indefinitely; this bounds how long the harvest waits for it before taking what is
    // already buffered.
    internal static readonly TimeSpan HarvestGrace = TimeSpan.FromSeconds(2);

    // C-6 (review F1): each stream is bounded exactly like a background job's — the most recent
    // tail is kept and the result says how much was dropped. A script that floods stdout and
    // hangs used to hand the client everything it wrote until the clock fired (49 MB in 3 s).
    internal const int BufferCapacityChars = 1_000_000;

    public PowerShellService(ILogger<PowerShellService> log) : this((ILogger)log, null) { }

    // Test ctor accepting non-generic ILogger (+ optional shorter backstop for tests).
    public PowerShellService(ILogger log, TimeSpan? backstopTimeout = null)
    {
        _log = log;
        _backstop = backstopTimeout ?? DefaultBackstop;
    }

    /// <summary>
    /// The overload every internal caller uses (disk, storage, security, firewall, network,
    /// audio, file streams, …): none of them asked for a clock, all of them parse stdout, so a
    /// backstop expiry has to be loud. It surfaces as a caller-facing <see cref="TimeoutException"/>
    /// whose message is the reason — never as a plausible-looking parse of partial output.
    /// </summary>
    public async Task<PSResult> RunAsync(string command, CancellationToken ct = default)
    {
        var result = await RunAsync(command, null, ct);
        if (result.TimedOut)
            throw new TimeoutException(result.Errors.Length > 0 ? result.Errors[^1] : "timed out (execution backstop)");
        return result;
    }

    /// <inheritdoc />
    public async Task<PSResult> RunAsync(string command, TimeSpan? timeout, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PowerShellService));
        if (timeout is { } requested && requested <= TimeSpan.Zero)
            throw new ArgumentException(
                $"timeout must be positive, got {requested}; pass null for the execution backstop only.",
                nameof(timeout));
        ct.ThrowIfCancellationRequested();

        string? scriptFileToDelete = null;

        // Acquire the gate under the CALLER's token only. Both clocks — the per-call timeout and
        // the backstop — must bound this call's *execution*, not the time it spends queued behind
        // other callers; otherwise a caller deep in the queue can burn its entire budget just
        // waiting, and be cut off before its own (perfectly fine) command ever runs.
        await _gate.WaitAsync(ct);
        try
        {
            // Now that we hold the gate, start the clock: the earlier of the caller's timeout and
            // the backstop. The result says which one fired; a timeout equal to the backstop is
            // the caller's own clock (review F7).
            var (budget, backstopWon) = timeout is { } wanted && wanted <= _backstop
                ? (wanted, false)
                : (_backstop, true);
            using var clock = new CancellationTokenSource(budget);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, clock.Token);
            var token = linkedCts.Token;

            // Build the invocation under the CALLER's token only (review F14): the temp-script
            // write is ours, not the script's, and a clock that fired during it would leave
            // through the exception path instead of returning TimedOut. See PowerShellInvocation
            // for why stdin is NOT used and how the EncodedCommand / temp-file fallback works.
            var (arguments, tempScript) = await PowerShellInvocation.BuildArgumentsAsync(command, ct);
            scriptFileToDelete = tempScript;

            using var proc = new Process { StartInfo = PowerShellInvocation.CreateStartInfo(arguments) };
            proc.Start();

            // Register the kill BEFORE any await: if cancellation (caller or a clock) fires
            // early, the child must still be torn down or it orphans.
            using var ctReg = token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            // Close stdin immediately — the script is passed via the command line, and leaving
            // the pipe open would make PowerShell wait for input that never comes.
            proc.StandardInput.Close();

            // Read both streams concurrently to avoid pipe deadlock on large output. The pumps
            // run under the CALLER's token only (C-6): a clock kills the tree, which closes the
            // write ends, and the pumps then drain what the script wrote before it was killed —
            // usually the diagnosis. Cancelling the reads would throw that away.
            var stdoutPump = new Pump(proc.StandardOutput, ct);
            var stderrPump = new Pump(proc.StandardError, ct);

            try
            {
                await proc.WaitForExitAsync(token);
            }
            catch (OperationCanceledException) when (clock.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // The clock ended the wait; decided below from the clock itself, because a clock
                // that fired before the wait began (a very short budget, a slow Start) has
                // already killed the child and the wait then returns normally instead.
            }

            bool timedOut = clock.IsCancellationRequested && !ct.IsCancellationRequested;
            if (timedOut)
            {
                await HarvestAsync(stdoutPump.Completion, stderrPump.Completion, HarvestGrace, ct);
                var seconds = budget.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                var reason = backstopWon
                    ? $"timed out after {seconds}s (execution backstop)"
                    : $"timed out after {seconds}s";
                var harvestedStderr = stderrPump.Text;
                return new PSResult(
                    Success: false,
                    Stdout: stdoutPump.Text,
                    Stderr: ClixmlStderr.Decode(harvestedStderr),
                    ExitCode: -1,
                    // Review F6: an error the script wrote before it hung is still an error; the
                    // reason for the stop comes last so a caller can always find it at the end.
                    Errors: [.. ExtractErrors(harvestedStderr), reason],
                    TimedOut: true,
                    StdoutTrimmedChars: stdoutPump.TrimmedChars,
                    StderrTrimmedChars: stderrPump.TrimmedChars);
            }

            await stdoutPump.Completion;
            await stderrPump.Completion;
            var stdout = stdoutPump.Text;
            var stderr = stderrPump.Text;

            var errors = ExtractErrors(stderr);

            return new PSResult(
                Success: proc.ExitCode == 0 && errors.Length == 0,
                Stdout: stdout,
                // D-8: the raw CLIXML blob never reaches the model. Progress records are dropped
                // (there is no console to draw them on) and the remaining streams become prefixed
                // text; non-CLIXML stderr passes through untouched.
                Stderr: ClixmlStderr.Decode(stderr),
                ExitCode: proc.ExitCode,
                Errors: errors,
                StdoutTrimmedChars: stdoutPump.TrimmedChars,
                StderrTrimmedChars: stderrPump.TrimmedChars);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "PowerShell execution failed");
            return new PSResult(false, "", ex.Message, -1, new[] { ex.Message });
        }
        finally
        {
            if (scriptFileToDelete is not null)
            {
                try { File.Delete(scriptFileToDelete); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to delete temp script {Path}", scriptFileToDelete); }
            }
            _gate.Release();
        }
    }

    /// <summary>
    /// C-6: wait for the pumps to drain after a tree kill, but not forever — a survivor holding
    /// a pipe would otherwise hold the gate too. Whatever is buffered when the grace runs out is
    /// the harvest. The caller's cancellation still throws.
    /// </summary>
    internal static async Task HarvestAsync(Task stdout, Task stderr, TimeSpan grace, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(grace, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TimeoutException) { /* a survivor still holds the pipe: take what was read */ }
        catch (Exception) { /* a pump faulted; its buffer is still the best answer available */ }
    }

    /// <summary>
    /// C-6: one redirected stream, read in chunks into a bounded buffer that can be inspected
    /// before the read has finished — that is what makes the partial output of a killed script
    /// available, and the bound is what keeps a flooding script from handing the client
    /// everything it wrote (<see cref="BufferCapacityChars"/>, the tail kept, like a job's).
    /// </summary>
    internal sealed class Pump
    {
        private readonly BoundedTextBuffer _buffer = new(BufferCapacityChars);

        public Task Completion { get; }

        public Pump(StreamReader reader, CancellationToken ct) => Completion = RunAsync(reader, ct);

        public string Text => _buffer.Snapshot();

        public long TrimmedChars => _buffer.TrimmedChars;

        private async Task RunAsync(StreamReader reader, CancellationToken ct)
        {
            var chunk = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(chunk.AsMemory(), ct)) > 0)
                _buffer.Append(chunk.AsSpan(0, read));
        }
    }

    /// <summary>
    /// Extracts the lines that should count as errors from a child's raw stderr.
    /// </summary>
    /// <remarks>
    /// Windows PowerShell 5.1 with redirected stderr wraps its error/warning/progress/verbose
    /// streams in a CLIXML document (a <c>#&lt; CLIXML</c> header line followed by
    /// <c>&lt;Objs&gt;</c> XML). Benign records land there too — e.g. an <c>Obj S="progress"</c>
    /// "Preparing modules for first use." on first-touch module import, or <c>S S="warning"</c>
    /// from Write-Warning — so non-empty stderr does NOT mean the command failed. Only genuine
    /// <c>&lt;S S="Error"&gt;</c> records count against Success. Non-CLIXML stderr (native children
    /// write raw bytes) and unparseable CLIXML (raw bytes interleaved with it) fall back to the
    /// plain line split. Parsing lives in <see cref="ClixmlStderr"/>, shared with the stderr
    /// decoder so the two cannot drift.
    /// </remarks>
    internal static string[] ExtractErrors(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return Array.Empty<string>();

        if (!ClixmlStderr.TryParseRecords(stderr, out var records))
            return stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return records
            .Where(r => string.Equals(r.Stream, "Error", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

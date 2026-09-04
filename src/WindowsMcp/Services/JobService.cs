using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class JobService : IJobService
{
    private sealed class Job
    {
        public required string Id { get; init; }
        public required string CommandPreview { get; init; }
        public required Process Process { get; init; }
        public required int Pid { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public required BoundedTextBuffer Stdout { get; init; }
        public required BoundedTextBuffer Stderr { get; init; }
        public required string? TempScript { get; init; }
        public required CancellationTokenSource Backstop { get; init; }
        public required Task StdoutPump { get; init; }
        public required Task StderrPump { get; init; }

        // Mutable state below is only touched under the service lock.
        public string State = "running";
        public string? PendingReason;     // "cancelled"/"timedOut" recorded by the killer; the
                                          // monitor task is the single writer of the final State.
        public DateTime? EndedAtUtc;
        public int? ExitCode;
        public Task Monitor = Task.CompletedTask;
    }

    private readonly ILogger _log;
    private readonly Dictionary<string, Job> _jobs = new();
    private readonly object _lock = new();
    private readonly TimeSpan _backstop;
    private readonly int _maxRunning;
    private readonly int _maxFinishedRetained;
    private readonly int _bufferCapChars;
    private int _seq;
    private bool _disposed;

    // Generous by design: background jobs exist FOR the long stuff (installers, DISM). The
    // backstop only guards against a genuinely runaway/hung child holding a process slot forever.
    private static readonly TimeSpan DefaultBackstop = TimeSpan.FromMinutes(60);

    public JobService(ILogger<JobService> log) : this((ILogger)log) { }

    // Test ctor accepting non-generic ILogger + tunable limits.
    internal JobService(
        ILogger log,
        TimeSpan? backstop = null,
        int maxRunning = 8,
        int maxFinishedRetained = 32,
        int bufferCapChars = 1_000_000)
    {
        _log = log;
        _backstop = backstop ?? DefaultBackstop;
        _maxRunning = maxRunning;
        _maxFinishedRetained = maxFinishedRetained;
        _bufferCapChars = bufferCapChars;
    }

    public async Task<JobInfo> StartAsync(string command, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(JobService));
        ct.ThrowIfCancellationRequested();

        string id;
        lock (_lock)
        {
            int running = _jobs.Values.Count(j => j.State == "running");
            if (running >= _maxRunning)
                throw new InvalidOperationException(
                    $"Job limit reached ({running} running, max {_maxRunning}). " +
                    "Cancel a job ('job cancel') or wait for one to finish.");
            id = "j" + (++_seq);
        }

        // Same invocation build as the foreground Powershell tool — see PowerShellInvocation.
        var (arguments, tempScript) = await PowerShellInvocation.BuildArgumentsAsync(command, ct);

        var proc = new Process { StartInfo = PowerShellInvocation.CreateStartInfo(arguments) };
        var backstopCts = new CancellationTokenSource(_backstop);
        try
        {
            proc.Start();
        }
        catch
        {
            backstopCts.Dispose();
            proc.Dispose();
            if (tempScript is not null)
            {
                try { File.Delete(tempScript); } catch { /* best-effort */ }
            }
            throw;
        }

        // Close stdin immediately — the script rides the command line; an open pipe would make
        // PowerShell wait for input that never comes (and stdin is redirected to keep the child
        // off our MCP JSON-RPC channel).
        proc.StandardInput.Close();

        var stdout = new BoundedTextBuffer(_bufferCapChars);
        var stderr = new BoundedTextBuffer(_bufferCapChars);
        var job = new Job
        {
            Id = id,
            CommandPreview = command.Length <= 120 ? command : command[..120],
            Process = proc,
            Pid = proc.Id,
            StartedAtUtc = DateTime.UtcNow,
            Stdout = stdout,
            Stderr = stderr,
            TempScript = tempScript,
            Backstop = backstopCts,
            StdoutPump = PumpAsync(proc.StandardOutput, stdout),
            StderrPump = PumpAsync(proc.StandardError, stderr),
        };

        // The backstop (and Cancel) only RECORD intent and kill; the monitor task alone writes
        // the final state after the process actually exits, so no state is ever written twice.
        backstopCts.Token.Register(() => KillJob(job, "timedOut"));

        lock (_lock)
        {
            _jobs[id] = job;
        }
        job.Monitor = MonitorAsync(job);

        return Snapshot(job);
    }

    public JobInfo? GetStatus(string id)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(id, out var job) ? Snapshot(job) : null;
        }
    }

    public JobOutput? GetOutput(string id, int tailChars = 0)
    {
        Job? job;
        string state;
        int? exitCode;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out job)) return null;
            state = job.State;
            exitCode = job.ExitCode;
        }
        // A finished job's buffer was decoded once by the monitor, so Tail() is already text. A
        // running job's has not been — decode a copy on the way out (D-9). PowerShell flushes whole
        // <Objs> documents, and the decoder drops a trailing partial one, so this usually succeeds
        // mid-run; when it cannot, the raw stream passes through exactly as before.
        var stderrText = state == "running"
            ? TailOf(ClixmlStderr.Decode(job.Stderr.Snapshot()), tailChars)
            : job.Stderr.Tail(tailChars);

        return new JobOutput(
            job.Id, state,
            job.Stdout.Tail(tailChars), stderrText,
            exitCode,
            job.Stdout.TrimmedChars, job.Stderr.TrimmedChars);
    }

    private static string TailOf(string text, int chars) =>
        chars <= 0 || chars >= text.Length ? text : text[^chars..];

    /// <summary>
    /// Rewrites a finished job's stderr buffer from CLIXML into readable text. Windows PowerShell
    /// 5.1 wraps every non-stdout stream in CLIXML when stderr is redirected; D-8 handles that for
    /// the foreground service, but a job's stream is captured incrementally and can only be decoded
    /// once it is complete. Best-effort by construction: <see cref="ClixmlStderr.Decode"/> returns
    /// the input unchanged for non-CLIXML or unparseable input, and a buffer whose head was trimmed
    /// has lost the "#&lt; CLIXML" marker, so it stays raw.
    /// </summary>
    private static void DecodeStderr(BoundedTextBuffer stderr)
    {
        var raw = stderr.Snapshot();
        if (raw.Length == 0) return;
        var decoded = ClixmlStderr.Decode(raw);
        if (!ReferenceEquals(decoded, raw) && decoded != raw) stderr.ReplaceAll(decoded);
    }

    public bool Cancel(string id)
    {
        Job? job;
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out job) || job.State != "running") return false;
        }
        KillJob(job, "cancelled");
        return true;
    }

    public JobInfo[] List()
    {
        lock (_lock)
        {
            return _jobs.Values.Select(Snapshot).ToArray();
        }
    }

    /// <summary>The monitor task for a job — awaited by tests for deterministic completion. Null when unknown.</summary>
    internal Task? WhenCompleted(string id)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(id, out var job) ? job.Monitor : null;
        }
    }

    private void KillJob(Job job, string reason)
    {
        lock (_lock)
        {
            if (job.State == "running" && job.PendingReason is null)
                job.PendingReason = reason;
        }
        try { job.Process.Kill(entireProcessTree: true); } catch { /* already exited/disposed */ }
    }

    private async Task MonitorAsync(Job job)
    {
        try
        {
            await job.Process.WaitForExitAsync(CancellationToken.None);
            int exitCode = job.Process.ExitCode;
            await Task.WhenAll(job.StdoutPump, job.StderrPump);

            // D-9: decode the now-complete CLIXML stderr ONCE, before the state flips to a terminal
            // value — so no reader can ever observe a finished job together with raw XML. After this
            // the buffer holds readable text, which keeps Tail(), Length and TrimmedChars consistent
            // with what `job output` returns, at no per-read cost.
            DecodeStderr(job.Stderr);

            lock (_lock)
            {
                job.ExitCode = exitCode;
                job.EndedAtUtc = DateTime.UtcNow;
                job.State = job.PendingReason ?? (exitCode == 0 ? "completed" : "failed");
                EvictFinishedOverRetentionLocked();
            }
        }
        catch (Exception ex)
        {
            // Shutdown races (the process disposed under us) land here; never crash the host.
            _log.LogWarning(ex, "Job {Id} monitor failed", job.Id);
            lock (_lock)
            {
                if (job.State == "running")
                {
                    job.EndedAtUtc = DateTime.UtcNow;
                    job.State = job.PendingReason ?? "failed";
                }
            }
        }
        finally
        {
            if (job.TempScript is not null)
            {
                try { File.Delete(job.TempScript); }
                catch (Exception ex) { _log.LogWarning(ex, "Failed to delete temp script {Path}", job.TempScript); }
            }
            try { job.Backstop.Dispose(); } catch { /* best-effort teardown */ }
            try { job.Process.Dispose(); } catch { /* best-effort teardown */ }
        }
    }

    private static async Task PumpAsync(StreamReader reader, BoundedTextBuffer buffer)
    {
        var chunk = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(chunk, 0, chunk.Length)) > 0)
                buffer.Append(chunk.AsSpan(0, n));
        }
        catch { /* stream torn down with the process */ }
    }

    // Caller must hold _lock.
    private void EvictFinishedOverRetentionLocked()
    {
        var finished = _jobs.Values.Where(j => j.State != "running").ToArray();
        if (finished.Length <= _maxFinishedRetained) return;
        foreach (var victim in finished
                     .OrderBy(j => j.EndedAtUtc ?? DateTime.MinValue)
                     .Take(finished.Length - _maxFinishedRetained))
        {
            _jobs.Remove(victim.Id);
        }
    }

    private static JobInfo Snapshot(Job j) => new(
        j.Id, j.State, j.Pid, j.CommandPreview,
        j.StartedAtUtc, j.EndedAtUtc, j.ExitCode,
        j.Stdout.Length, j.Stderr.Length,
        j.Stdout.TrimmedChars, j.Stderr.TrimmedChars);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Job[] jobs;
        lock (_lock)
        {
            jobs = _jobs.Values.ToArray();
            _jobs.Clear();
        }
        foreach (var job in jobs)
        {
            try { job.Process.Kill(entireProcessTree: true); } catch { /* best-effort teardown */ }
            try { job.Backstop.Dispose(); } catch { /* best-effort teardown */ }
            try { job.Process.Dispose(); } catch { /* best-effort teardown */ }
        }
    }
}

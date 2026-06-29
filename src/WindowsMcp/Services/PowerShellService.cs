using System.Diagnostics;
using System.Text;
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
    private static readonly TimeSpan DefaultBackstop = TimeSpan.FromMinutes(10);

    // System PowerShell is guaranteed present at this path on Windows 7+.
    // Avoids the broken InitialSessionState.CreateDefault2 path in the PS NuGet
    // SDK when running under PublishSingleFile=true: Assembly.Location returns ""
    // in single-file mode, then Path.Combine chokes inside PSSnapInReader.
    // Snap-in DLLs are not bundled in the single-file image.
    private const string PowerShellExe =
        @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    public PowerShellService(ILogger<PowerShellService> log) : this((ILogger)log, null) { }

    // Test ctor accepting non-generic ILogger (+ optional shorter backstop for tests).
    public PowerShellService(ILogger log, TimeSpan? backstopTimeout = null)
    {
        _log = log;
        _backstop = backstopTimeout ?? DefaultBackstop;
    }

    public async Task<PSResult> RunAsync(string command, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PowerShellService));
        ct.ThrowIfCancellationRequested();

        // Combine the caller's token with the backstop so the gate is never held indefinitely.
        using var timeoutCts = new CancellationTokenSource(_backstop);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linkedCts.Token;

        await _gate.WaitAsync(token);
        try
        {
            // -NoProfile: skip user profile load (faster, deterministic)
            // -NonInteractive: never prompt
            // -ExecutionPolicy Bypass: allow scripts
            // -Command -: read command from stdin
            var psi = new ProcessStartInfo
            {
                FileName = PowerShellExe,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            // Register the kill BEFORE writing stdin: if cancellation (caller or backstop) fires
            // during the write, the child must still be torn down or it orphans.
            using var ctReg = token.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            // Write the command then close stdin so PowerShell exits when done.
            await proc.StandardInput.WriteAsync(command.AsMemory(), token);
            await proc.StandardInput.FlushAsync(token);
            proc.StandardInput.Close();

            // Read both streams concurrently to avoid pipe deadlock on large output.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(token);
            var stderrTask = proc.StandardError.ReadToEndAsync(token);

            await proc.WaitForExitAsync(token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var errors = string.IsNullOrEmpty(stderr)
                ? Array.Empty<string>()
                : stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return new PSResult(
                Success: proc.ExitCode == 0 && errors.Length == 0,
                Stdout: stdout,
                Stderr: stderr,
                ExitCode: proc.ExitCode,
                Errors: errors);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "PowerShell execution failed");
            return new PSResult(false, "", ex.Message, -1, new[] { ex.Message });
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

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
    private bool _disposed;

    // System PowerShell is guaranteed present at this path on Windows 7+.
    // Avoids the broken InitialSessionState.CreateDefault2 path in the PS NuGet
    // SDK when running under PublishSingleFile=true: Assembly.Location returns ""
    // in single-file mode, then Path.Combine chokes inside PSSnapInReader.
    // Snap-in DLLs are not bundled in the single-file image.
    private const string PowerShellExe =
        @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    public PowerShellService(ILogger<PowerShellService> log) => _log = log;

    // Test ctor accepting non-generic ILogger
    public PowerShellService(ILogger log) => _log = log;

    public async Task<PSResult> RunAsync(string command, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PowerShellService));
        ct.ThrowIfCancellationRequested();

        await _gate.WaitAsync(ct);
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

            // Write the command then close stdin so PowerShell exits when done.
            await proc.StandardInput.WriteAsync(command.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
            proc.StandardInput.Close();

            // Read both streams concurrently to avoid pipe deadlock on large output.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            // Wire cancellation: kill the process tree if ct fires mid-execution.
            using var ctReg = ct.Register(() =>
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            });

            await proc.WaitForExitAsync(ct);
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

using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class PowerShellService : IPowerShellService
{
    private readonly ILogger _log;
    private Runspace _runspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _callCount;
    private DateTime _runspaceCreated;
    private bool _disposed;
    private const int RestartAfterCalls = 1000;
    private static readonly TimeSpan RestartAfter = TimeSpan.FromMinutes(30);

    public PowerShellService(ILogger<PowerShellService> log)
    {
        _log = log;
        _runspace = CreateRunspace();
    }

    // Test ctor accepting non-generic ILogger
    public PowerShellService(ILogger log)
    {
        _log = log;
        _runspace = CreateRunspace();
    }

    private Runspace CreateRunspace()
    {
        // CreateDefault2() loads only the core engine — no snap-ins that require
        // full PS install DLLs not shipped with the NuGet SDK package.
        var iss = InitialSessionState.CreateDefault2();
        var rs = RunspaceFactory.CreateRunspace(iss);
        rs.Open();
        _runspaceCreated = DateTime.UtcNow;
        _callCount = 0;
        _log.LogInformation("PowerShell runspace created");
        return rs;
    }

    public async Task<PSResult> RunAsync(string command, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PowerShellService));
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct);
        try
        {
            MaybeRestartRunspace();
            // Increment before invocation so recycling still triggers when calls fail.
            _callCount++;
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(command);

            var output = new StringBuilder();
            var errors = new List<string>();
            try
            {
                // ct.Register fires Stop() if the token is cancelled while ps.Invoke runs.
                // PowerShell.Stop is the documented way to interrupt a synchronous Invoke.
                using var ctReg = ct.Register(() =>
                {
                    try { ps.Stop(); }
                    catch { /* runspace may be in a state where Stop throws; best-effort */ }
                });

                var results = await Task.Run(() => ps.Invoke(), ct);
                foreach (var item in results)
                    output.AppendLine(item?.ToString() ?? "");
                foreach (var err in ps.Streams.Error)
                    errors.Add(err.ToString());

                int exitCode = ps.HadErrors ? 1 : 0;
                if (!ps.HadErrors)
                {
                    using var psExit = PowerShell.Create();
                    psExit.Runspace = _runspace;
                    psExit.AddScript("if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }");
                    var exitResults = psExit.Invoke();
                    if (exitResults.Count > 0 && int.TryParse(exitResults[0]?.ToString(), out var parsed))
                        exitCode = parsed;
                }

                return new PSResult(
                    Success: !ps.HadErrors,
                    Stdout: output.ToString(),
                    Stderr: string.Join('\n', errors),
                    ExitCode: exitCode,
                    Errors: errors.ToArray());
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PowerShell execution failed");
                return new PSResult(false, "", ex.Message, -1, new[] { ex.Message });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void MaybeRestartRunspace()
    {
        if (_callCount >= RestartAfterCalls || DateTime.UtcNow - _runspaceCreated > RestartAfter)
        {
            _log.LogInformation(
                "Recycling PowerShell runspace ({Calls} calls / {Age} age)",
                _callCount,
                DateTime.UtcNow - _runspaceCreated);
            _runspace.Dispose();
            _runspace = CreateRunspace();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runspace.Dispose();
        _gate.Dispose();
    }
}

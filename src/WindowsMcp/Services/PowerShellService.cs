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
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct);
        try
        {
            MaybeRestartRunspace();
            using var ps = PowerShell.Create();
            ps.Runspace = _runspace;
            ps.AddScript(command);

            var output = new StringBuilder();
            var errors = new List<string>();
            try
            {
                var results = await Task.Run(() => ps.Invoke(), ct);
                foreach (var item in results)
                    output.AppendLine(item?.ToString() ?? "");
                foreach (var err in ps.Streams.Error)
                    errors.Add(err.ToString());

                _callCount++;
                return new PSResult(
                    Success: !ps.HadErrors,
                    Stdout: output.ToString(),
                    Stderr: string.Join('\n', errors),
                    ExitCode: ps.HadErrors ? 1 : 0,
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

    public void Dispose() => _runspace.Dispose();
}

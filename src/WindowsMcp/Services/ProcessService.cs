using System.Diagnostics;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ProcessService : IProcessService
{
    public Task<ProcessDto[]> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var dtos = Process.GetProcesses()
            .Select(p =>
            {
                string? path = null;
                try { path = p.MainModule?.FileName; }
                catch { /* system/protected processes throw on MainModule access */ }
                return new ProcessDto(p.Id, p.ProcessName, path, p.WorkingSet64 / 1024 / 1024);
            })
            .ToArray();

        return Task.FromResult(dtos);
    }

    public Task KillAsync(int pid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Process.GetProcessById(pid).Kill();
        return Task.CompletedTask;
    }

    public Task<int> StartDetachedAsync(string command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Split command into executable + arguments on the first space.
        // Handles quoted executables: "C:\My App\foo.exe" arg1 arg2
        string exe;
        string args = string.Empty;

        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new ArgumentException("Unmatched opening quote in command");
            exe = command[1..closingQuote];
            args = command[(closingQuote + 1)..].TrimStart();
        }
        else
        {
            int spaceIdx = command.IndexOf(' ');
            if (spaceIdx < 0)
            {
                exe = command;
            }
            else
            {
                exe = command[..spaceIdx];
                args = command[(spaceIdx + 1)..].TrimStart();
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            CreateNoWindow = false
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        return Task.FromResult(process.Id);
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class ProcessService : IProcessService
{
    private readonly IWmiService _wmi;

    public ProcessService(IWmiService wmi) => _wmi = wmi;

    public Task<ProcessDto[]> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Process wrappers hold native handles (opened on WorkingSet64/MainModule access);
        // dispose every one after projecting to DTOs, or handles leak per call.
        var processes = Process.GetProcesses();
        try
        {
            var dtos = processes
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
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    public Task KillAsync(int pid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var process = Process.GetProcessById(pid);
        process.Kill();
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

        // Detached child keeps running after we dispose the wrapper (UseShellExecute=false,
        // no handles we own); dispose only releases our handle to it.
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        return Task.FromResult(process.Id);
    }

    public async Task<ProcessDetailDto> InspectAsync(int pid, CancellationToken ct = default)
    {
        // Parent PID + command line come from WMI (System.Diagnostics exposes neither).
        int? parentPid = null;
        string? commandLine = null;
        string? name = null;

        var rows = await _wmi.QueryAsync("Win32_Process", null, $"ProcessId={pid}", ct);
        if (rows.Length > 0 && rows[0] is IDictionary<string, object> d)
        {
            if (d.TryGetValue("ParentProcessId", out var pp) && pp is not null)
                parentPid = Convert.ToInt32(pp);
            if (d.TryGetValue("CommandLine", out var cl))
                commandLine = cl?.ToString();
            if (d.TryGetValue("Name", out var nm))
                name = nm?.ToString();
        }

        // Start time + loaded modules come from the live Process. Module enumeration throws
        // Access-Denied on protected/higher-integrity processes — capture that as a note rather
        // than failing the whole inspection.
        DateTime? startUtc = null;
        var modules = new List<ModuleInfo>();
        string? modulesError = null;
        try
        {
            using var proc = Process.GetProcessById(pid);
            name ??= proc.ProcessName;
            try { startUtc = proc.StartTime.ToUniversalTime(); } catch { /* may be denied */ }
            try
            {
                foreach (ProcessModule m in proc.Modules)
                    modules.Add(new ModuleInfo(m.ModuleName, m.FileName));
            }
            catch (Exception ex)
            {
                modulesError = ex.Message;
            }
        }
        catch (ArgumentException)
        {
            // Process exited between the WMI query and here; return what WMI gave us.
        }

        return new ProcessDetailDto(pid, name, parentPid, commandLine, startUtc, modulesError, modules.ToArray());
    }
}

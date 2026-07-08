using System.Management;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>Typed projection of a Win32_Process row (parse boundary handles CIM_DATETIME).</summary>
public readonly record struct Win32ProcRow(
    int Pid, int ParentPid, string Name, DateTime? CreationUtc, string? CommandLine, long MemoryMb);

/// <summary>Pure process-lineage logic: parse, orphan classification, root grouping, signals.</summary>
public static class ProcessLineage
{
    /// <summary>Project one raw WMI dictionary row into a typed row; null if it has no ProcessId.</summary>
    public static Win32ProcRow? From(IDictionary<string, object> row)
    {
        if (!row.TryGetValue("ProcessId", out var pidObj) || pidObj is null) return null;
        int pid = Convert.ToInt32(pidObj);
        int ppid = row.TryGetValue("ParentProcessId", out var pp) && pp is not null ? Convert.ToInt32(pp) : 0;
        string name = row.TryGetValue("Name", out var nm) && nm is not null ? nm.ToString()! : "";
        string? cmd = row.TryGetValue("CommandLine", out var cl) ? cl?.ToString() : null;

        DateTime? created = null;
        if (row.TryGetValue("CreationDate", out var cd) && cd is string s && s.Length > 0)
        {
            try { created = ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime(); }
            catch { /* unparseable CIM_DATETIME -> null */ }
        }

        long memMb = 0;
        if (row.TryGetValue("WorkingSetSize", out var ws) && ws is not null)
        {
            try { memMb = Convert.ToInt64(ws) / 1024 / 1024; } catch { /* leave 0 */ }
        }
        return new Win32ProcRow(pid, ppid, name, created, cmd, memMb);
    }

    public static ProcessLineageDto[] Classify(IReadOnlyList<Win32ProcRow> rows, DateTime nowUtc)
    {
        var byId = new Dictionary<int, Win32ProcRow>();
        foreach (var r in rows) byId[r.Pid] = r;

        bool ParentAlive(Win32ProcRow p)
        {
            if (!byId.TryGetValue(p.ParentPid, out var par)) return false;
            if (p.CreationUtc is DateTime c && par.CreationUtc is DateTime pc && pc > c) return false; // recycled
            return true;
        }

        int RootOf(Win32ProcRow p)
        {
            var seen = new HashSet<int>();
            var cur = p;
            int hops = 0;
            while (ParentAlive(cur) && hops++ < 64 && seen.Add(cur.Pid))
                cur = byId[cur.ParentPid];
            return cur.Pid;
        }

        var result = new List<ProcessLineageDto>(rows.Count);
        foreach (var p in rows)
        {
            bool alive = ParentAlive(p);
            string? parentName = alive && byId.TryGetValue(p.ParentPid, out var par) ? par.Name : null;
            int? age = p.CreationUtc is DateTime c
                ? (int)Math.Max(0, (nowUtc - c).TotalMinutes) : null;
            result.Add(new ProcessLineageDto(
                p.Pid, p.Name, p.ParentPid, parentName, p.CommandLine, p.CreationUtc, age,
                !alive, RuntimeKind(p.Name), IsSystemAdjacent(p), RootOf(p), p.MemoryMb));
        }
        return result.ToArray();
    }

    public static ProcessGroupDto[] GroupByRoot(ProcessLineageDto[] procs)
    {
        var byId = procs.ToDictionary(p => p.Pid);
        return procs.GroupBy(p => p.RootPid)
            .Select(g =>
            {
                byId.TryGetValue(g.Key, out var root);
                return new ProcessGroupDto(g.Key, root?.Name ?? "", root?.StartTimeUtc,
                    g.Count(), g.Select(x => x.Pid).OrderBy(x => x).ToArray());
            })
            .OrderByDescending(x => x.DescendantCount).ToArray();
    }

    static readonly Dictionary<string, string> KindMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node.exe"] = "node",
        ["python.exe"] = "python", ["python3.exe"] = "python", ["pythonw.exe"] = "python",
        ["dotnet.exe"] = "dotnet",
        ["pwsh.exe"] = "shell", ["powershell.exe"] = "shell", ["cmd.exe"] = "shell",
        ["bash.exe"] = "shell", ["wsl.exe"] = "shell",
        ["chrome.exe"] = "browser", ["msedge.exe"] = "browser", ["firefox.exe"] = "browser",
    };

    static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "svchost.exe", "fontdrvhost.exe", "dwm.exe",
        "userinit.exe", "explorer.exe",
    };

    public static string RuntimeKind(string name)
    {
        if (KindMap.TryGetValue(name, out var k)) return k;
        if (name.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return "python";
        return SystemNames.Contains(name) ? "native" : "other";
    }

    public static bool IsSystemAdjacent(Win32ProcRow p)
        => SystemNames.Contains(p.Name) || p.ParentPid is 0 or 4;
}

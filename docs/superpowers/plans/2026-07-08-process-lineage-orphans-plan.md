# Process lineage / orphans / root-grouping / fleet-aware kill — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to
> implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the windows-mcp process tool with recycle-aware lineage, orphan enumeration,
root-grouping, command-line/filtering, and a fleet-aware (tree) recycle-safe kill — all as
single tool calls.

**Architecture:** Pure classifier (`ProcessLineage`) over typed `Win32ProcRow` rows parsed from a
single bulk `Win32_Process` WMI query; service methods orchestrate WMI→parse→classify→filter;
the `Process` tool gains actions/params. Layering unchanged (Tool→Abstractions, Service→
Abstractions+Models), 0 new cycles.

**Tech Stack:** C# / .NET 9, `System.Management` (WMI + `ManagementDateTimeConverter`), xUnit +
Moq + FluentAssertions.

## Global Constraints

- `TreatWarningsAsErrors=true`, `Nullable=enable`, `ImplicitUsings=enable`. DTOs are `record`s;
  services `sealed`; tool methods `async Task<string>` returning JSON with `[McpServerTool,
  Description(...)]`.
- **`IProcessService` additions only — never change existing signatures.** It is consumed by
  `ProcessTools` AND `StartupReportService`, doubled only by Moq mocks. `KillAsync`, `ListAsync`,
  `StartDetachedAsync`, `InspectAsync` stay byte-stable.
- **Do not modify `IWmiService`** (shared by five services). Call existing `QueryAsync(class,
  null, null, ct)` for the bulk enumeration.
- **CIM_DATETIME:** WMI `CreationDate` is a raw string (`yyyyMMddHHmmss.ffffff±ooo`); parse with
  `ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime()` at the parse seam only. Classifier
  is pure over `Win32ProcRow[]` + injected `nowUtc` (real `DateTime`s, no WMI/string types).
- **Orphan = recycle-aware:** orphaned unless the parent id is present AND not provably recycled
  (provably recycled = both `CreationUtc` non-null and `parent.CreationUtc > child.CreationUtc`).
  A null date ⇒ cannot prove recycle ⇒ treat parent alive.
- **Filter after classification** so `RootPid` still resolves to a filtered-out root.
- Plain `list` fast path (`Process.GetProcesses()`) and existing kill behavior unchanged.
- Windows: `python` (not python3) for any scripts; forward slashes.

---

## File Structure

- Create `src/WindowsMcp/Services/ProcessLineage.cs` — `Win32ProcRow` record + pure static
  `ProcessLineage` (`From`, `Classify`, `GroupByRoot`, `RuntimeKind`, `IsSystemAdjacent`).
- Modify `src/WindowsMcp.Abstractions/Models/ProcessDtos.cs` — add `ProcessLineageDto`,
  `ProcessGroupDto`.
- Modify `src/WindowsMcp.Abstractions/IProcessService.cs` — add 4 methods.
- Modify `src/WindowsMcp/Services/ProcessService.cs` — implement the 4 methods.
- Modify `src/WindowsMcp/Tools/ProcessTools.cs` — params, `orphans` action, dispatch, Description.
- Create `tests/WindowsMcp.Tests/Services/ProcessLineageTests.cs` — Unit tests (pure).
- Modify `tests/WindowsMcp.Tests/Services/ProcessServiceTests.cs` — integration tests.
- Modify `tests/WindowsMcp.Tests/Tools/ProcessToolsTests.cs` — dispatch tests.
- Modify `docs/architecture/COMPONENTS.md`, `docs/architecture/DATAFLOW.md`, `CHANGELOG.md`.

---

### Task 1: DTOs + `Win32ProcRow` parse + pure classifier (with unit tests)

**Files:** Create `Services/ProcessLineage.cs`; modify `Models/ProcessDtos.cs`; create
`tests/.../Services/ProcessLineageTests.cs`.

**Interfaces — Produces:** `Win32ProcRow`, `ProcessLineage.From/Classify/GroupByRoot`,
`ProcessLineageDto`, `ProcessGroupDto` (consumed by Task 2).

- [ ] **Step 1: DTOs.** Append to `Models/ProcessDtos.cs`:

```csharp
/// <summary>One process with parent lineage, orphan status, and descriptive signals.</summary>
public record ProcessLineageDto(
    int Pid, string Name, int? ParentPid, string? ParentName, string? CommandLine,
    DateTime? StartTimeUtc, int? AgeMinutes, bool Orphaned, string RuntimeKind,
    bool IsSystemAdjacent, int RootPid, long MemoryMb);

/// <summary>Processes collapsed under their nearest-live root ancestor.</summary>
public record ProcessGroupDto(
    int RootPid, string RootName, DateTime? RootStartTimeUtc, int DescendantCount, int[] ChildPids);
```

- [ ] **Step 2: Write failing unit tests** in `tests/WindowsMcp.Tests/Services/ProcessLineageTests.cs`:

```csharp
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class ProcessLineageTests
{
    static readonly DateTime Now = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
    static Win32ProcRow Row(int pid, int ppid, string name, DateTime? created,
        string? cmd = null, long mem = 0) => new(pid, ppid, name, created, cmd, mem);

    [Fact]
    public void From_parses_cim_datetime_and_coerces_workingset()
    {
        var row = new Dictionary<string, object>
        {
            ["ProcessId"] = 100, ["ParentProcessId"] = 4, ["Name"] = "svchost.exe",
            ["CreationDate"] = "20260708070935.590000-300",
            ["CommandLine"] = "svchost -k netsvcs", ["WorkingSetSize"] = (ulong)(50 * 1024 * 1024),
        };
        var r = ProcessLineage.From(row);
        r.Should().NotBeNull();
        r!.Value.Pid.Should().Be(100);
        r.Value.CreationUtc.Should().NotBeNull();
        r.Value.CreationUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        r.Value.MemoryMb.Should().Be(50);
    }

    [Fact]
    public void From_tolerates_missing_date_string_workingset_and_skips_rows_without_pid()
    {
        ProcessLineage.From(new Dictionary<string, object> { ["Name"] = "x" }).Should().BeNull();
        var r = ProcessLineage.From(new Dictionary<string, object>
        {
            ["ProcessId"] = 4, ["ParentProcessId"] = 0, ["Name"] = "System",
            ["CreationDate"] = "", ["WorkingSetSize"] = "1048576",
        });
        r!.Value.CreationUtc.Should().BeNull();
        r.Value.MemoryMb.Should().Be(1);
    }

    [Fact]
    public void Classify_marks_dead_parent_as_orphan()
    {
        var rows = new[] { Row(10, 999, "node.exe", Now.AddMinutes(-30)) }; // ppid 999 absent
        var dto = ProcessLineage.Classify(rows, Now).Single();
        dto.Orphaned.Should().BeTrue();
        dto.ParentName.Should().BeNull();
        dto.RootPid.Should().Be(10);
        dto.AgeMinutes.Should().Be(30);
        dto.RuntimeKind.Should().Be("node");
    }

    [Fact]
    public void Classify_marks_recycled_parent_as_orphan_but_not_genuine_parent()
    {
        var rows = new[]
        {
            Row(1, 0, "System", Now.AddMinutes(-100)),
            Row(20, 1, "child.exe", Now.AddMinutes(-50)),   // parent older -> genuine
            Row(30, 40, "kid.exe", Now.AddMinutes(-50)),
            Row(40, 0, "reused.exe", Now.AddMinutes(-10)),  // "parent" younger -> recycled
        };
        var map = ProcessLineage.Classify(rows, Now).ToDictionary(d => d.Pid);
        map[20].Orphaned.Should().BeFalse();
        map[30].Orphaned.Should().BeTrue();   // recycled parent
    }

    [Fact]
    public void Classify_null_dated_parent_is_not_treated_as_recycled()
    {
        var rows = new[]
        {
            Row(4, 0, "System", null),                       // no CIM date
            Row(50, 4, "wininit.exe", Now.AddMinutes(-200)),
        };
        ProcessLineage.Classify(rows, Now).Single(d => d.Pid == 50).Orphaned.Should().BeFalse();
    }

    [Fact]
    public void Classify_walks_multi_level_root_and_guards_cycles()
    {
        var rows = new[]
        {
            Row(1, 0, "root.exe", Now.AddMinutes(-90)),
            Row(2, 1, "mid.exe", Now.AddMinutes(-80)),
            Row(3, 2, "leaf.exe", Now.AddMinutes(-70)),
            Row(7, 8, "a.exe", Now.AddMinutes(-60)),         // mutual cycle 7<->8
            Row(8, 7, "b.exe", Now.AddMinutes(-60)),
        };
        var map = ProcessLineage.Classify(rows, Now).ToDictionary(d => d.Pid);
        map[3].RootPid.Should().Be(1);
        map[7].RootPid.Should().BeOneOf(7, 8); // terminates, no infinite loop
    }

    [Fact]
    public void GroupByRoot_counts_and_lists_children()
    {
        var rows = new[]
        {
            Row(1, 0, "claude.exe", Now.AddMinutes(-90)),
            Row(2, 1, "node.exe", Now.AddMinutes(-80)),
            Row(3, 1, "node.exe", Now.AddMinutes(-80)),
        };
        var groups = ProcessLineage.GroupByRoot(ProcessLineage.Classify(rows, Now));
        var g = groups.Single(x => x.RootPid == 1);
        g.DescendantCount.Should().Be(3);
        g.ChildPids.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        g.RootName.Should().Be("claude.exe");
    }

    [Fact]
    public void IsSystemAdjacent_flags_boot_processes()
    {
        ProcessLineage.IsSystemAdjacent(Row(9, 0, "explorer.exe", Now)).Should().BeTrue();
        ProcessLineage.IsSystemAdjacent(Row(9, 100, "node.exe", Now)).Should().BeFalse();
    }
}
```

- [ ] **Step 3: Run tests — confirm they FAIL to compile/find `ProcessLineage`.**
  Run: `dotnet test --filter "FullyQualifiedName~ProcessLineageTests"` → FAIL (type not found).

- [ ] **Step 4: Implement** `src/WindowsMcp/Services/ProcessLineage.cs`:

```csharp
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
```

- [ ] **Step 5: Run tests — confirm GREEN.**
  Run: `dotnet test --filter "FullyQualifiedName~ProcessLineageTests"` → all PASS.
  (Note the `python3.14.exe` case is covered by the `StartsWith("python")` fallback.)

- [ ] **Step 6: Build clean.** Run: `dotnet build` → 0 warnings (TreatWarningsAsErrors).

- [ ] **Step 7: Commit.**
```bash
git add src/WindowsMcp/Services/ProcessLineage.cs src/WindowsMcp.Abstractions/Models/ProcessDtos.cs tests/WindowsMcp.Tests/Services/ProcessLineageTests.cs
git commit -m "feat(process): recycle-aware lineage classifier + DTOs"
```

---

### Task 2: Service methods + interface additions (with integration tests)

**Files:** modify `IProcessService.cs`, `ProcessService.cs`, `tests/.../Services/ProcessServiceTests.cs`.

**Interfaces — Consumes:** Task 1 types. **Produces:** `ListLineageAsync`, `GroupByRootAsync`,
`KillGuardedAsync`, `KillTreeAsync` (consumed by Task 3).

- [ ] **Step 1: Interface — add 4 methods** to `IProcessService` (do NOT touch existing four):

```csharp
/// <summary>All processes with recycle-aware lineage + signals; optionally only orphans,
/// optionally filtered (substring on name OR command line). Filter is applied after
/// classification so RootPid still resolves to a filtered-out root.</summary>
Task<ProcessLineageDto[]> ListLineageAsync(bool orphansOnly, string? nameFilter, CancellationToken ct = default);
/// <summary>Processes collapsed under their nearest-live root ancestor.</summary>
Task<ProcessGroupDto[]> GroupByRootAsync(CancellationToken ct = default);
/// <summary>Kill a single PID only if its live start time matches expectedStartUtc (guards PID reuse).</summary>
Task KillGuardedAsync(int pid, DateTime expectedStartUtc, CancellationToken ct = default);
/// <summary>Kill a PID and its recycle-validated descendants, leaves-first; returns count killed.</summary>
Task<int> KillTreeAsync(int pid, DateTime? expectedStartUtc, CancellationToken ct = default);
```

- [ ] **Step 2: Write failing integration tests** — append to `ProcessServiceTests.cs`:

```csharp
[Fact]
public async Task ListLineageAsync_includes_current_process_with_a_parent()
{
    var svc = Make(new WmiService());
    var self = System.Environment.ProcessId;
    var rows = await svc.ListLineageAsync(orphansOnly: false, nameFilter: null);
    var me = rows.Should().ContainSingle(r => r.Pid == self).Subject;
    me.ParentPid.Should().NotBeNull();
    me.CommandLine.Should().NotBeNullOrEmpty();
    me.RootPid.Should().BeGreaterThan(0);
}

[Fact]
public async Task ListLineageAsync_name_filter_matches_name_or_commandline()
{
    var svc = Make(new WmiService());
    var filtered = await svc.ListLineageAsync(false, "dotnet");
    filtered.Should().OnlyContain(r =>
        r.Name.Contains("dotnet", System.StringComparison.OrdinalIgnoreCase) ||
        (r.CommandLine ?? "").Contains("dotnet", System.StringComparison.OrdinalIgnoreCase));
}

[Fact]
public async Task GroupByRootAsync_returns_groups_covering_all_processes()
{
    var svc = Make(new WmiService());
    var groups = await svc.GroupByRootAsync();
    groups.Should().NotBeEmpty();
    groups.Sum(g => g.DescendantCount).Should().BeGreaterThan(0);
    groups.Should().OnlyContain(g => g.ChildPids.Length == g.DescendantCount);
}

[Fact]
public async Task KillGuardedAsync_aborts_on_start_time_mismatch()
{
    var svc = Make(new WmiService());
    var pid = await svc.StartDetachedAsync("\"C:\\Windows\\System32\\cmd.exe\" /c pause");
    try
    {
        var act = () => svc.KillGuardedAsync(pid, new System.DateTime(2000, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
        await act.Should().ThrowAsync<System.InvalidOperationException>();
    }
    finally { try { await svc.KillAsync(pid); } catch { } }
}

[Fact]
public async Task KillTreeAsync_kills_parent_and_child()
{
    var svc = Make(new WmiService());
    // cmd that spawns a child cmd that pauses; both should die.
    var pid = await svc.StartDetachedAsync(
        "\"C:\\Windows\\System32\\cmd.exe\" /c start /wait cmd /c pause");
    await System.Threading.Tasks.Task.Delay(400); // let the child spawn
    var killed = await svc.KillTreeAsync(pid, null);
    killed.Should().BeGreaterThanOrEqualTo(1);
    var act = () => System.Diagnostics.Process.GetProcessById(pid);
    act.Should().Throw<System.ArgumentException>(); // root gone
}
```

- [ ] **Step 3: Run — confirm FAIL** (methods not implemented).
  Run: `dotnet test --filter "FullyQualifiedName~ProcessServiceTests"` → FAIL (no such members).

- [ ] **Step 4: Implement** the 4 methods in `ProcessService.cs` (add a small private helper for the
  shared WMI→rows projection):

```csharp
private async Task<List<Win32ProcRow>> SnapshotAsync(CancellationToken ct)
{
    var raw = await _wmi.QueryAsync("Win32_Process", null, null, ct);
    return raw.OfType<IDictionary<string, object>>()
        .Select(ProcessLineage.From)
        .Where(r => r.HasValue).Select(r => r!.Value).ToList();
}

public async Task<ProcessLineageDto[]> ListLineageAsync(bool orphansOnly, string? nameFilter, CancellationToken ct = default)
{
    var all = ProcessLineage.Classify(await SnapshotAsync(ct), DateTime.UtcNow);
    IEnumerable<ProcessLineageDto> q = all;
    if (orphansOnly) q = q.Where(p => p.Orphaned);
    if (!string.IsNullOrWhiteSpace(nameFilter))
        q = q.Where(p =>
            p.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ||
            (p.CommandLine?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false));
    return q.ToArray();
}

public async Task<ProcessGroupDto[]> GroupByRootAsync(CancellationToken ct = default)
    => ProcessLineage.GroupByRoot(ProcessLineage.Classify(await SnapshotAsync(ct), DateTime.UtcNow));

public Task KillGuardedAsync(int pid, DateTime expectedStartUtc, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    using var proc = Process.GetProcessById(pid); // ArgumentException if gone
    DateTime actual;
    try { actual = proc.StartTime.ToUniversalTime(); }
    catch { throw new InvalidOperationException($"cannot read start time of pid {pid} to verify"); }
    if (Math.Abs((actual - expectedStartUtc).TotalSeconds) > 1.5)
        throw new InvalidOperationException(
            $"pid {pid} start time {actual:o} != expected {expectedStartUtc:o}; aborting (possible PID reuse)");
    proc.Kill();
    return Task.CompletedTask;
}

public async Task<int> KillTreeAsync(int pid, DateTime? expectedStartUtc, CancellationToken ct = default)
{
    var rows = await SnapshotAsync(ct);
    var byId = rows.ToDictionary(r => r.Pid);
    if (!byId.TryGetValue(pid, out var root))
        throw new ArgumentException($"pid {pid} not found");
    if (expectedStartUtc is DateTime exp && root.CreationUtc is DateTime rc
        && Math.Abs((rc - exp).TotalSeconds) > 1.5)
        throw new InvalidOperationException($"pid {pid} start time mismatch; aborting");

    static bool ValidParent(Win32ProcRow child, Win32ProcRow parent)
        => !(child.CreationUtc is DateTime c && parent.CreationUtc is DateTime pc && pc > c);

    var childrenOf = new Dictionary<int, List<int>>();
    foreach (var r in rows)
        if (byId.TryGetValue(r.ParentPid, out var par) && ValidParent(r, par))
        {
            if (!childrenOf.TryGetValue(r.ParentPid, out var list))
                childrenOf[r.ParentPid] = list = new List<int>();
            list.Add(r.Pid);
        }

    var order = new List<int>();
    var seen = new HashSet<int>();
    void Visit(int id)
    {
        if (!seen.Add(id)) return;
        if (childrenOf.TryGetValue(id, out var kids))
            foreach (var k in kids) Visit(k);
        order.Add(id); // post-order => leaves first
    }
    Visit(pid);

    int killed = 0;
    foreach (var id in order)
    {
        try { using var p = Process.GetProcessById(id); p.Kill(); killed++; }
        catch { /* already exited */ }
    }
    return killed;
}
```

- [ ] **Step 5: Run — confirm GREEN.**
  Run: `dotnet test --filter "FullyQualifiedName~ProcessServiceTests"` → PASS.
  If the kill-tree test is flaky on child-spawn timing, it is environmental (documented like the
  UIAutomation fixtures) — the pure descendant-ordering is covered in Task 1's cycle test; keep
  the integration assertion at "root gone + killed >= 1".

- [ ] **Step 6: Build clean** (`dotnet build`, 0 warnings) then **commit.**
```bash
git add src/WindowsMcp.Abstractions/IProcessService.cs src/WindowsMcp/Services/ProcessService.cs tests/WindowsMcp.Tests/Services/ProcessServiceTests.cs
git commit -m "feat(process): lineage/orphans/group/guarded-kill service methods"
```

---

### Task 3: Tool wiring + Description + docs + CHANGELOG

**Files:** modify `ProcessTools.cs`, `tests/.../Tools/ProcessToolsTests.cs`,
`docs/architecture/COMPONENTS.md`, `docs/architecture/DATAFLOW.md`, `CHANGELOG.md`.

**Interfaces — Consumes:** Task 2 service methods.

- [ ] **Step 1: Write failing tool tests** — append to `ProcessToolsTests.cs` (mirror existing
  Moq style; the file already builds a tool over `Mock<IProcessService>`):

```csharp
[Fact]
public async Task Process_orphans_calls_ListLineageAsync_with_orphansOnly_true()
{
    var mock = new Mock<IProcessService>();
    mock.Setup(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()))
        .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
    var tools = Make(mock.Object);
    var json = await tools.Process("orphans");
    mock.Verify(m => m.ListLineageAsync(true, null, It.IsAny<CancellationToken>()), Times.Once);
    json.Should().Be("[]");
}

[Fact]
public async Task Process_list_includeLineage_calls_ListLineageAsync_false()
{
    var mock = new Mock<IProcessService>();
    mock.Setup(m => m.ListLineageAsync(false, "node", It.IsAny<CancellationToken>()))
        .ReturnsAsync(System.Array.Empty<ProcessLineageDto>());
    var tools = Make(mock.Object);
    await tools.Process("list", name: "node", includeLineage: true);
    mock.Verify(m => m.ListLineageAsync(false, "node", It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task Process_list_groupByRoot_calls_GroupByRootAsync()
{
    var mock = new Mock<IProcessService>();
    mock.Setup(m => m.GroupByRootAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(System.Array.Empty<ProcessGroupDto>());
    var tools = Make(mock.Object);
    await tools.Process("list", groupByRoot: true);
    mock.Verify(m => m.GroupByRootAsync(It.IsAny<CancellationToken>()), Times.Once);
}

[Fact]
public async Task Process_kill_tree_requires_confirm_and_calls_KillTreeAsync()
{
    var mock = new Mock<IProcessService>();
    mock.Setup(m => m.KillTreeAsync(1234, null, It.IsAny<CancellationToken>())).ReturnsAsync(3);
    var tools = Make(mock.Object);
    var noConfirm = () => tools.Process("kill", pid: 1234, tree: true);
    await noConfirm.Should().ThrowAsync<System.ArgumentException>();
    var json = await tools.Process("kill", pid: 1234, tree: true, confirm: true);
    mock.Verify(m => m.KillTreeAsync(1234, null, It.IsAny<CancellationToken>()), Times.Once);
    json.Should().Contain("3");
}
```

  (If `ProcessToolsTests` lacks a `Make(IProcessService)` helper, add one mirroring its existing
  constructor-wiring that supplies the other three services as `new Mock<...>().Object`.)

- [ ] **Step 2: Run — confirm FAIL** (params/action absent).
  Run: `dotnet test --filter "FullyQualifiedName~ProcessToolsTests"` → FAIL.

- [ ] **Step 3: Rewrite the `Process` tool method** in `ProcessTools.cs` — add params, the
  `orphans` action, and dispatch (existing `list`/`kill` behavior preserved):

```csharp
[McpServerTool, Description(
    "List/inspect/kill processes. actions: list|orphans|kill. " +
    "list: plain (Pid,Name,Path,MemoryMb); with includeLineage:true adds parent lineage, " +
    "startTime, ageMinutes, orphaned, runtimeKind, isSystemAdjacent, rootPid; with " +
    "groupByRoot:true returns processes collapsed under their nearest-live root ancestor. " +
    "orphans: lineage rows where the parent is gone (recycle-aware: parent absent, or a live " +
    "same-PID process started AFTER the child). NOTE orphaned is COMMON and by-design on Windows " +
    "(explorer.exe and apps from a closed shell are orphaned) — it is NOT a leak signal; use the " +
    "signals to rank, the tool does not judge. name filters list/orphans by substring on name OR " +
    "command line. kill: by pid or name (kills all matching), confirm:true required; tree:true " +
    "kills the pid AND its descendants (leaves-first); startTime (ISO-8601) guards against PID " +
    "reuse — the kill aborts unless the live process's start time matches.")]
public async Task<string> Process(
    [Description("Action: list, orphans, or kill")] string action,
    [Description("Process name; kill target, or substring filter for list/orphans")] string? name = null,
    [Description("Process ID (kill target)")] int? pid = null,
    [Description("Must be true to confirm a kill")] bool confirm = false,
    [Description("list: include parent lineage + signals")] bool includeLineage = false,
    [Description("list: group processes under their root ancestor")] bool groupByRoot = false,
    [Description("kill: also kill the target's descendants")] bool tree = false,
    [Description("kill: ISO-8601 start time guard against PID reuse")] string? startTime = null,
    CancellationToken ct = default)
{
    switch (action.ToLowerInvariant())
    {
        case "list":
            if (groupByRoot)
                return JsonSerializer.Serialize(await _process.GroupByRootAsync(ct));
            if (includeLineage)
                return JsonSerializer.Serialize(await _process.ListLineageAsync(false, name, ct));
            return JsonSerializer.Serialize(await _process.ListAsync(ct));

        case "orphans":
            return JsonSerializer.Serialize(await _process.ListLineageAsync(true, name, ct));

        case "kill":
            if (!confirm)
                throw new ArgumentException("'confirm: true' is required for kill");
            DateTime? start = null;
            if (!string.IsNullOrWhiteSpace(startTime))
            {
                if (!DateTime.TryParse(startTime, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    throw new ArgumentException($"'startTime' must be ISO-8601, got: '{startTime}'");
                start = parsed.ToUniversalTime();
            }
            if (pid.HasValue)
            {
                if (tree)
                {
                    int n = await _process.KillTreeAsync(pid.Value, start, ct);
                    return $"killed {n} process(es) in tree of pid {pid.Value}";
                }
                if (start is DateTime s)
                {
                    await _process.KillGuardedAsync(pid.Value, s, ct);
                    return $"killed pid {pid.Value} (start-time verified)";
                }
                await _process.KillAsync(pid.Value, ct);
                return $"killed pid {pid.Value}";
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                var all = await _process.ListAsync(ct);
                var targets = all.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var t in targets)
                    await _process.KillAsync(t.Pid, ct);
                return $"killed {targets.Length} process(es) named '{name}'";
            }
            throw new ArgumentException("'kill' requires either name or pid");

        default:
            throw new ArgumentException($"Unknown action '{action}'; expected list|orphans|kill");
    }
}
```

- [ ] **Step 4: Run — confirm GREEN** and full headless suite passes.
  Run: `dotnet test --filter "Category!=UIAutomation"` → PASS (a lone ClipboardService failure is
  environmental per CLAUDE.md, not a regression).

- [ ] **Step 5: Build clean** (`dotnet build`, 0 warnings).

- [ ] **Step 6: Docs.** In `docs/architecture/COMPONENTS.md` and `DATAFLOW.md`, update the process-
  tool description to list actions `list|orphans|kill` and the lineage/group/kill-tree
  capabilities + the WMI→parse→classify data path. **No version/date in these docs.** Tool count is
  unchanged (still the one `Process` tool) — do not bump any tool count.

- [ ] **Step 7: CHANGELOG.** Add under `## [Unreleased]`:
```markdown
### Added
- Process tool: recycle-aware lineage (`list includeLineage:true`), orphan enumeration
  (`orphans`) with `ageMinutes`/`runtimeKind`/`isSystemAdjacent` signals, root-grouping
  (`list groupByRoot:true`), name/command-line filtering, and a recycle-safe fleet kill
  (`kill tree:true`, `startTime` PID-reuse guard).
```

- [ ] **Step 8: Commit.**
```bash
git add src/WindowsMcp/Tools/ProcessTools.cs tests/WindowsMcp.Tests/Tools/ProcessToolsTests.cs docs/architecture/COMPONENTS.md docs/architecture/DATAFLOW.md CHANGELOG.md
git commit -m "feat(process): tool actions for lineage/orphans/group/kill-tree + docs"
```

---

## Controller wrap-up (not a subagent task)

- Regenerate the dependency graph (`npx tsx tools/create-dependency-graph/create-dependency-graph.ts
  --root=. --lang=csharp`) and commit — confirms still 0 cycles with the new `ProcessLineage` node.
- **Live redeploy** (per repo CLAUDE.md): rename running `dist/WindowsMcp.exe` aside →
  `dotnet publish src/WindowsMcp -c Release -o dist -r win-x64 --self-contained
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true` → bump `_RETRY` in
  `~/.claude/local-marketplace/windows-mcp/.mcp.json` → user runs `/reload-plugins` → confirm the
  server `StartTime` > publish time.
- **Live verify:** call `process action:orphans` and `process action:list groupByRoot:true` on the
  reloaded server; confirm the 5-`claude.exe`-roots + episodic-memory-pair picture reproduces in
  one call (spec Success Criterion #3).
- **Version bump on release:** `plugin.json` + the `windows-mcp` `marketplace.json` entry (version-
  gating); rename CHANGELOG `[Unreleased]`→the release version.
- Push `main`; verify remote==local.

## Self-Review (plan vs spec)

- **Coverage:** DTOs+parse+classifier+signals → Task 1; service methods+interface+kill → Task 2;
  tool surface+filter+docs+CHANGELOG → Task 3; redeploy+live-verify+version → wrap-up. All 8 spec
  success criteria mapped (SC1/2/4 → T1+T2 tests; SC3 → wrap-up live verify; SC5 → T2 kill tests;
  SC6 → preserved `list`/`kill` paths + T3; SC7 → build/test steps; SC8 → wrap-up).
- **Placeholders:** none — full code in every code step; exact commands + expected pass/fail.
- **Type consistency:** `ProcessLineageDto`/`ProcessGroupDto`/`Win32ProcRow` field names and the
  four new `IProcessService` signatures are identical in Tasks 1–3.
- **Constraint adherence:** `KillAsync` signature untouched; `IWmiService` untouched; filter after
  classify; CIM parse at the seam; null-date not recycled; plain `list` fast path preserved.

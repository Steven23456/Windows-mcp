using System.Security.Principal;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Startup;

namespace WindowsMcp.Services;

/// <summary>
/// Aggregates persistence/startup data from the registry, task scheduler, services, file
/// system, Winsock catalog and process list into a single <see cref="StartupReportDto"/>.
/// Each section is gathered independently; a section that fails records an error and yields
/// empty rather than failing the whole report.
/// </summary>
public sealed class StartupReportService : IStartupReportService
{
    private const string ApprovedRun = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
    private const string ApprovedFolder = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder";

    // hive, run-key path, matching StartupApproved path (null = no approval gate, e.g. RunOnce).
    // Explorer records approvals (incl. for 32-bit WOW6432Node Run values) in the single
    // non-WOW StartupApproved\Run key, so WOW6432Node Run maps to ApprovedRun too.
    private static readonly (string Hive, string KeyPath, string? Approved)[] RunKeys =
    {
        ("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Run", ApprovedRun),
        ("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", null),
        ("HKLM", "Software\\Microsoft\\Windows\\CurrentVersion\\Run", ApprovedRun),
        ("HKLM", "Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce", null),
        ("HKLM", "Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run", ApprovedRun),
    };

    private readonly IProcessService _process;
    private readonly IRegistryService _registry;
    private readonly IServiceControlService _services;
    private readonly ITaskSchedulerService _tasks;
    private readonly IFileSystemService _fs;
    private readonly ILspEnumerator _lsp;
    private readonly IAuthenticodeInspector _auth;
    private readonly IShortcutResolver _shortcuts;

    public StartupReportService(
        IProcessService process,
        IRegistryService registry,
        IServiceControlService services,
        ITaskSchedulerService tasks,
        IFileSystemService fs,
        ILspEnumerator lsp,
        IAuthenticodeInspector auth,
        IShortcutResolver shortcuts)
    {
        _process = process;
        _registry = registry;
        _services = services;
        _tasks = tasks;
        _fs = fs;
        _lsp = lsp;
        _auth = auth;
        _shortcuts = shortcuts;
    }

    public async Task<StartupReportDto> BuildAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        var processes = await Safe(() => BuildProcessesAsync(ct), "processes", errors, Array.Empty<ProcessEntry>());
        var run = await Safe(() => BuildRunEntriesAsync(ct), "run", errors, Array.Empty<RunEntry>());
        var folders = await Safe(() => BuildStartupFoldersAsync(ct), "startup_folders", errors, Array.Empty<StartupFolderEntry>());
        var tasks = await Safe(() => BuildTasksAsync(ct), "scheduled_tasks", errors, Array.Empty<StartupTaskEntry>());
        var services = await Safe(() => BuildServicesAsync(ct), "services", errors, Array.Empty<StartupServiceEntry>());
        var hosts = await Safe(() => BuildHostsAsync(ct), "hosts", errors, Array.Empty<HostsEntry>());
        var lsp = await Safe(() => Task.FromResult(BuildLsp()), "lsp", errors, Array.Empty<LspProviderEntry>());
        var shell = await Safe(() => BuildShellExtensionsAsync(ct), "shell_extensions", errors, Array.Empty<ShellExtensionEntry>());

        return new StartupReportDto(BuildHeader(), processes, run, folders, tasks, services, hosts, lsp, shell, errors.ToArray());
    }

    private static StartupHeader BuildHeader() =>
        new(Environment.MachineName, Environment.OSVersion.VersionString, IsElevated(), DateTime.UtcNow);

    private (bool Trusted, string? Signer) Sig(string? path)
    {
        var info = _auth.Inspect(path);
        return (info.Trusted, info.Signer);
    }

    private async Task<ProcessEntry[]> BuildProcessesAsync(CancellationToken ct)
    {
        var procs = await _process.ListAsync(ct);
        return procs.Select(p =>
        {
            var (t, s) = Sig(p.Path);
            return new ProcessEntry(p.Pid, p.Name, p.Path, p.MemoryMb, t, s);
        }).ToArray();
    }

    private async Task<RunEntry[]> BuildRunEntriesAsync(CancellationToken ct)
    {
        var list = new List<RunEntry>();
        foreach (var (hive, keyPath, approvedPath) in RunKeys)
        {
            var approved = approvedPath is null
                ? new Dictionary<string, byte[]?>()
                : ToFlagMap(await _registry.EnumerateValuesAsync(hive, approvedPath, ct));

            foreach (var v in await _registry.EnumerateValuesAsync(hive, keyPath, ct))
            {
                string command = v.Data?.ToString() ?? string.Empty;
                bool enabled = !approved.TryGetValue(v.Name, out var flag) || StartupApproval.IsEnabled(flag);
                var (t, s) = Sig(CommandTarget.ResolveExe(command));
                list.Add(new RunEntry(hive, keyPath, v.Name, command, enabled, CommandTarget.Exists(command), t, s));
            }
        }
        return list.ToArray();
    }

    private async Task<StartupFolderEntry[]> BuildStartupFoldersAsync(CancellationToken ct)
    {
        var userApproved = ToFlagMap(await _registry.EnumerateValuesAsync("HKCU", ApprovedFolder, ct));
        var commonApproved = ToFlagMap(await _registry.EnumerateValuesAsync("HKLM", ApprovedFolder, ct));

        var list = new List<StartupFolderEntry>();
        await AddFolderAsync("User", Environment.GetFolderPath(Environment.SpecialFolder.Startup), userApproved, list, ct);
        await AddFolderAsync("Common", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), commonApproved, list, ct);
        return list.ToArray();
    }

    private async Task AddFolderAsync(string scope, string folder, Dictionary<string, byte[]?> approved,
        List<StartupFolderEntry> list, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(folder)) return;

        string[] files;
        try { files = await _fs.ListAsync(folder, ct); }
        catch { return; }

        foreach (var file in files)
        {
            string full = Path.IsPathRooted(file) ? file : Path.Combine(folder, file);
            string name = Path.GetFileName(full);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            bool enabled = !approved.TryGetValue(name, out var flag) || StartupApproval.IsEnabled(flag);
            string? target = _shortcuts.ResolveTarget(full);
            var (t, s) = Sig(target);
            bool targetExists = target is not null && File.Exists(target);
            list.Add(new StartupFolderEntry(scope, name, target ?? full, enabled, targetExists, t, s));
        }
    }

    private async Task<StartupTaskEntry[]> BuildTasksAsync(CancellationToken ct)
    {
        var tasks = await _tasks.ListDetailedAsync(ct);
        var list = new List<StartupTaskEntry>();
        foreach (var t in tasks)
        {
            bool startupTriggered = t.Triggers.Any(x =>
                x.Equals("Logon", StringComparison.OrdinalIgnoreCase) ||
                x.Equals("Boot", StringComparison.OrdinalIgnoreCase));
            bool targetExists = t.ActionPath is not null && CommandTarget.Exists(t.ActionPath);
            bool missingTarget = t.ActionPath is not null && !targetExists;

            if (!startupTriggered && !missingTarget) continue;   // not startup-relevant

            var (tr, s) = Sig(t.ActionPath);
            list.Add(new StartupTaskEntry(t.Path, t.State, t.ActionPath, t.ActionArguments, t.Triggers, targetExists, tr, s));
        }
        return list.ToArray();
    }

    private async Task<StartupServiceEntry[]> BuildServicesAsync(CancellationToken ct)
    {
        var services = await _services.ListAsync(ct);
        var list = new List<StartupServiceEntry>();
        foreach (var s in services.Where(s => s.StartType is "Automatic" or "Boot" or "System"))
        {
            string? bin = null;
            try
            {
                var v = await _registry.GetAsync("HKLM", $"SYSTEM\\CurrentControlSet\\Services\\{s.Name}", "ImagePath", ct);
                bin = v.Data?.ToString();
            }
            catch { /* missing ImagePath value/key */ }

            var (t, sig) = Sig(CommandTarget.ResolveExe(NormalizeImagePath(bin)));
            list.Add(new StartupServiceEntry(s.Name, s.DisplayName, s.Status, s.StartType, bin, t, sig));
        }
        return list.ToArray();
    }

    private async Task<HostsEntry[]> BuildHostsAsync(CancellationToken ct)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        string text = await _fs.ReadTextAsync(path, 1_000_000, "utf-8", ct);
        var list = new List<HostsEntry>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].StartsWith('#')) break;
                list.Add(new HostsEntry(parts[0], parts[i]));
            }
        }
        return list.ToArray();
    }

    private LspProviderEntry[] BuildLsp() =>
        _lsp.Enumerate().Select(p =>
        {
            var (t, s) = Sig(p.ProviderPath);
            return new LspProviderEntry(p.CatalogEntryId, p.ProtocolName, p.ProviderPath, t, s);
        }).ToArray();

    private async Task<ShellExtensionEntry[]> BuildShellExtensionsAsync(CancellationToken ct)
    {
        var list = new List<ShellExtensionEntry>();
        await AddOverlaysAsync(list, ct);
        await AddContextMenusAsync("*", "ContextMenu(*)", list, ct);
        await AddContextMenusAsync("Directory", "ContextMenu(Directory)", list, ct);
        await AddContextMenusAsync("Directory\\Background", "ContextMenu(Directory\\Background)", list, ct);
        await AddContextMenusAsync("Folder", "ContextMenu(Folder)", list, ct);
        return list.ToArray();
    }

    private async Task AddOverlaysAsync(List<ShellExtensionEntry> list, CancellationToken ct)
    {
        const string key = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellIconOverlayIdentifiers";
        foreach (var name in await _registry.EnumerateSubKeysAsync("HKLM", key, ct))
            list.Add(await BuildShellExtAsync("ShellIconOverlay", $"{key}\\{name}", name, ct));
    }

    private async Task AddContextMenusAsync(string classPath, string category, List<ShellExtensionEntry> list, CancellationToken ct)
    {
        string key = $"SOFTWARE\\Classes\\{classPath}\\shellex\\ContextMenuHandlers";
        foreach (var name in await _registry.EnumerateSubKeysAsync("HKLM", key, ct))
            list.Add(await BuildShellExtAsync(category, $"{key}\\{name}", name, ct));
    }

    private async Task<ShellExtensionEntry> BuildShellExtAsync(string category, string handlerKey, string subKeyName, CancellationToken ct)
    {
        string? def = await DefaultValueAsync("HKLM", handlerKey, ct);
        string? clsid = LooksLikeGuid(def) ? def : (LooksLikeGuid(subKeyName) ? subKeyName : null);
        string? dll = await ResolveClsidDllAsync(clsid, ct);
        var (t, s) = Sig(dll);
        return new ShellExtensionEntry(category, clsid ?? subKeyName, dll, t, s);
    }

    private async Task<string?> ResolveClsidDllAsync(string? clsid, CancellationToken ct)
    {
        if (!LooksLikeGuid(clsid)) return null;
        foreach (var basePath in new[] { "SOFTWARE\\Classes\\CLSID", "SOFTWARE\\WOW6432Node\\Classes\\CLSID" })
        {
            string? dll = await DefaultValueAsync("HKLM", $"{basePath}\\{clsid}\\InprocServer32", ct);
            if (!string.IsNullOrEmpty(dll))
                return Environment.ExpandEnvironmentVariables(dll.Trim('"'));
        }
        return null;
    }

    private async Task<string?> DefaultValueAsync(string hive, string path, CancellationToken ct)
    {
        var vals = await _registry.EnumerateValuesAsync(hive, path, ct);
        return vals.FirstOrDefault(v => v.Name == "(default)")?.Data?.ToString();
    }

    private static Dictionary<string, byte[]?> ToFlagMap(RegistryValueDto[] vals) =>
        vals.ToDictionary(v => v.Name, v => v.Data is byte[] b ? b : null, StringComparer.OrdinalIgnoreCase);

    private static string? NormalizeImagePath(string? p) =>
        string.IsNullOrEmpty(p) ? p : (p.StartsWith("\\??\\") ? p[4..] : p);

    private static bool LooksLikeGuid(string? s) =>
        s is not null && s.StartsWith('{') && s.EndsWith('}') && Guid.TryParse(s, out _);

    private static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static async Task<T> Safe<T>(Func<Task<T>> build, string section, List<string> errors, T fallback)
    {
        try { return await build(); }
        catch (Exception ex) { errors.Add($"{section}: {ex.GetType().Name}: {ex.Message}"); return fallback; }
    }
}

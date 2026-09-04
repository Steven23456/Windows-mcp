using System.Collections;

namespace WindowsMcp.Hosting;

/// <summary>
/// Repairs the process environment when the MCP host launched us with a stripped-down block.
/// Claude Desktop (observed 1.46) starts stdio servers with roughly 18 variables and a
/// <c>PATHEXT</c> of just <c>.CPL</c> — so <c>powershell</c> could not resolve <c>git</c>,
/// <c>winget</c>, <c>dotnet</c> or <c>wsl</c> (no <c>.EXE</c> in the search list), and
/// <c>docker mcp</c> panicked on a missing <c>ProgramData</c>. Every child we spawn inherits
/// whatever we have, so fix it once, here, before anything else runs.
/// <para>
/// Policy: <b>never overwrite</b> a variable the host set (its PATH may be deliberate) except
/// <c>PATHEXT</c>, which is only corrected when it cannot resolve an <c>.exe</c>. Missing
/// variables are filled from the registry (machine, then user overlay; the two <c>Path</c>
/// values are joined, as the shell does) and then from well-known folder/system defaults.
/// </para>
/// </summary>
internal static class EnvironmentRepair
{
    /// <summary>What a stock Windows install carries; used only when the registry has nothing usable.</summary>
    internal const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC";

    /// <summary>Apply to the live process. Returns the names that were set or corrected (for the startup log).</summary>
    public static IReadOnlyList<string> Apply()
    {
        var process = ToDictionary(Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process));
        var machine = SafeRead(EnvironmentVariableTarget.Machine);
        var user = SafeRead(EnvironmentVariableTarget.User);
        return Apply(process, machine, user, SystemDefaults(), static (k, v) => Environment.SetEnvironmentVariable(k, v));
    }

    /// <summary>Pure core — all inputs injected so it is unit-testable without touching the real environment.</summary>
    internal static IReadOnlyList<string> Apply(
        IReadOnlyDictionary<string, string> process,
        IReadOnlyDictionary<string, string> machine,
        IReadOnlyDictionary<string, string> user,
        IReadOnlyDictionary<string, string> defaults,
        Action<string, string> set)
    {
        var changed = new List<string>();
        var have = new Dictionary<string, string>(process, StringComparer.OrdinalIgnoreCase);

        // Registry view, with Windows' documented merge rule for Path (machine first, then user).
        var registry = new Dictionary<string, string>(machine, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in user)
        {
            if (k.Equals("Path", StringComparison.OrdinalIgnoreCase) && registry.TryGetValue(k, out var m) && m.Length > 0)
                registry[k] = m.TrimEnd(';') + ";" + v;
            else
                registry[k] = v;
        }

        void Fill(string key, string value)
        {
            if (string.IsNullOrEmpty(value) || have.ContainsKey(key)) return;
            set(key, value);
            have[key] = value;
            changed.Add(key);
        }

        foreach (var (k, v) in registry) Fill(k, v);
        foreach (var (k, v) in defaults) Fill(k, v);

        // PATHEXT is the one value we will overwrite: without .EXE nothing resolves.
        if (!have.TryGetValue("PATHEXT", out var pathExt) || !HasExe(pathExt))
        {
            var repaired = registry.TryGetValue("PATHEXT", out var reg) && HasExe(reg) ? reg : DefaultPathExt;
            set("PATHEXT", repaired);
            have["PATHEXT"] = repaired;
            changed.Add("PATHEXT");
        }

        return changed;
    }

    internal static bool HasExe(string? pathExt) =>
        pathExt is not null &&
        pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(e => e.Equals(".EXE", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Values cmd.exe/explorer normally provide that are not registry-backed. Only consulted for
    /// names still absent after the registry pass.
    /// </summary>
    private static Dictionary<string, string> SystemDefaults()
    {
        static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var windows = Folder(Environment.SpecialFolder.Windows);
        var system = Folder(Environment.SpecialFolder.System);
        var temp = Environment.GetEnvironmentVariable("TEMP") ?? Path.GetTempPath().TrimEnd('\\');

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["ComSpec"] = Path.Combine(system, "cmd.exe"),
            ["ProgramData"] = Folder(Environment.SpecialFolder.CommonApplicationData),
            ["ProgramFiles"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Folder(Environment.SpecialFolder.ProgramFilesX86),
            ["ProgramW6432"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["CommonProgramFiles"] = Folder(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = Folder(Environment.SpecialFolder.CommonProgramFilesX86),
            ["CommonProgramW6432"] = Folder(Environment.SpecialFolder.CommonProgramFiles),
            ["TEMP"] = temp,
            ["TMP"] = temp,
            ["OS"] = "Windows_NT",
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(),
            ["PROCESSOR_ARCHITECTURE"] = Environment.Is64BitOperatingSystem ? "AMD64" : "x86",
        };
    }

    private static Dictionary<string, string> SafeRead(EnvironmentVariableTarget target)
    {
        try { return ToDictionary(Environment.GetEnvironmentVariables(target)); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, string> ToDictionary(IDictionary raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in raw)
        {
            if (e.Key is string k && e.Value is string v) d[k] = v;
        }
        return d;
    }
}

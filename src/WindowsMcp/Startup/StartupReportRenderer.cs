using System.Text;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Startup;

/// <summary>
/// Renders a <see cref="StartupReportDto"/> as a human-readable, section-grouped text block
/// (the companion to the structured JSON).
/// </summary>
public static class StartupReportRenderer
{
    public static string Render(StartupReportDto r)
    {
        var sb = new StringBuilder();
        var h = r.Header;
        sb.AppendLine("Windows-mcp Startup Report");
        sb.AppendLine($"Machine: {h.Machine}   OS: {h.OsVersion}   Elevated: {h.Elevated}   (UTC {h.TimestampUtc:yyyy-MM-dd HH:mm:ss})");

        Section(sb, "Processes", r.Processes.Length);
        foreach (var p in r.Processes)
            sb.AppendLine($"  [{p.Pid}] {p.Name}  {p.Path ?? "(path n/a)"}  {Sig(p.Trusted, p.Signer)}");

        Section(sb, "Run entries", r.RunEntries.Length);
        foreach (var e in r.RunEntries)
            sb.AppendLine($"  [{e.Hive}\\{e.KeyPath}] {e.Name} = {e.Command}  {Flag("enabled", e.Enabled)} {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Startup folders", r.StartupFolders.Length);
        foreach (var e in r.StartupFolders)
            sb.AppendLine($"  [{e.Scope}] {e.FileName} -> {e.Target}  {Flag("enabled", e.Enabled)} {Flag("target", e.TargetExists)} {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Scheduled tasks", r.ScheduledTasks.Length);
        foreach (var t in r.ScheduledTasks)
            sb.AppendLine($"  {t.Path} [{t.State}] -> {t.ActionPath ?? "(no exec action)"}  triggers=[{string.Join(",", t.Triggers)}]  {Flag("target", t.TargetExists)} {Sig(t.Trusted, t.Signer)}");

        Section(sb, "Auto-start services", r.Services.Length);
        foreach (var s in r.Services)
            sb.AppendLine($"  {s.Name} ({s.DisplayName}) [{s.Status}/{s.StartType}] -> {s.BinaryPath ?? "(path n/a)"}  {Sig(s.Trusted, s.Signer)}");

        Section(sb, "Hosts file", r.Hosts.Length);
        foreach (var e in r.Hosts)
            sb.AppendLine($"  {e.Ip}  {e.Host}");

        Section(sb, "Winsock LSP", r.Lsp.Length);
        foreach (var e in r.Lsp)
            sb.AppendLine($"  #{e.CatalogEntryId} {e.ProtocolName}  {e.ProviderPath ?? "(path n/a)"}  {Sig(e.Trusted, e.Signer)}");

        Section(sb, "Shell extensions", r.ShellExtensions.Length);
        foreach (var e in r.ShellExtensions)
            sb.AppendLine($"  [{e.Category}] {e.Clsid} -> {e.Dll ?? "(dll n/a)"}  {Sig(e.Trusted, e.Signer)}");

        if (r.Errors.Length > 0)
        {
            Section(sb, "Errors", r.Errors.Length);
            foreach (var e in r.Errors) sb.AppendLine($"  {e}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void Section(StringBuilder sb, string title, int count)
    {
        sb.AppendLine();
        sb.AppendLine($"== {title} ({count}) ==");
    }

    private static string Flag(string name, bool value) => $"{name}={(value ? "Y" : "N")}";

    private static string Sig(bool trusted, string? signer) =>
        trusted ? $"trusted={(signer is null ? "Y" : signer)}" : "trusted=N";
}

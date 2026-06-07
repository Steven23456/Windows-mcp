namespace WindowsMcp.Abstractions.Models;

/// <summary>Aggregated boot/persistence report (the HiJackThis-style snapshot).</summary>
public record StartupReportDto(
    StartupHeader Header,
    ProcessEntry[] Processes,
    RunEntry[] RunEntries,
    StartupFolderEntry[] StartupFolders,
    StartupTaskEntry[] ScheduledTasks,
    StartupServiceEntry[] Services,
    HostsEntry[] Hosts,
    LspProviderEntry[] Lsp,
    ShellExtensionEntry[] ShellExtensions,
    string[] Errors);

public record StartupHeader(string Machine, string OsVersion, bool Elevated, DateTime TimestampUtc);

public record ProcessEntry(int Pid, string Name, string? Path, long MemoryMb, bool Trusted, string? Signer);

/// <summary>A Run/RunOnce value, with its effective enabled state (StartupApproved-decoded).</summary>
public record RunEntry(
    string Hive, string KeyPath, string Name, string Command,
    bool Enabled, bool TargetExists, bool Trusted, string? Signer);

public record StartupFolderEntry(
    string Scope, string FileName, string Target,
    bool Enabled, bool TargetExists, bool Trusted, string? Signer);

public record StartupTaskEntry(
    string Path, string State, string? ActionPath, string? ActionArguments,
    string[] Triggers, bool TargetExists, bool Trusted, string? Signer);

public record StartupServiceEntry(
    string Name, string DisplayName, string Status, string StartType,
    string? BinaryPath, bool Trusted, string? Signer);

public record HostsEntry(string Ip, string Host);

public record LspProviderEntry(
    int CatalogEntryId, string ProtocolName, string? ProviderPath, bool Trusted, string? Signer);

public record ShellExtensionEntry(
    string Category, string Clsid, string? Dll, bool Trusted, string? Signer);

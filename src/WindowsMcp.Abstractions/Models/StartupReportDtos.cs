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
    DnsEntry[] Dns,
    LspProviderEntry[] Lsp,
    ShellExtensionEntry[] ShellExtensions,
    ControlPanelAppletEntry[] ControlPanelApplets,
    AccessibilityToolEntry[] AccessibilityTools,
    IfeoEntry[] ImageFileExecutionOptions,
    WinlogonHookEntry[] WinlogonHooks,
    AppInitDllEntry[] AppInitDlls,
    ActiveSetupEntry[] ActiveSetup,
    BrowserProxyEntry[] BrowserProxy,
    TrustedZoneEntry[] TrustedZone,
    string[] Errors);

public record StartupHeader(
    string Machine, string OsVersion, bool Elevated, DateTime TimestampUtc,
    string BootMode, string User, string? DefaultBrowser);

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

/// <summary>A configured DNS server on an active network adapter (rogue-DNS check).</summary>
public record DnsEntry(string Adapter, string Server);

public record LspProviderEntry(
    int CatalogEntryId, string ProtocolName, string? ProviderPath, bool Trusted, string? Signer);

public record ShellExtensionEntry(
    string Category, string Clsid, string? Dll, bool Trusted, string? Signer);

/// <summary>A registered Control Panel applet (.cpl) — a rogue-applet persistence vector.</summary>
public record ControlPanelAppletEntry(
    string Hive, string Name, string Path, bool TargetExists, bool Trusted, string? Signer);

/// <summary>An Accessibility Technology "StartExe" hook (utilman/AT-style hijack vector).</summary>
public record AccessibilityToolEntry(
    string Name, string? StartExe, bool TargetExists, bool Trusted, string? Signer);

/// <summary>An Image File Execution Options hook (Debugger / GlobalFlag / VerifierDlls).</summary>
public record IfeoEntry(string Image, string Kind, string Value, bool TargetExists, bool Trusted, string? Signer);

/// <summary>A Winlogon hook value (Shell / Userinit / Taskman / Notify).</summary>
public record WinlogonHookEntry(string Name, string Value, bool TargetExists, bool Trusted, string? Signer);

/// <summary>An AppInit_DLLs injection entry (per architecture view).</summary>
public record AppInitDllEntry(string Scope, string Dll, bool Enabled, bool TargetExists, bool Trusted, string? Signer);

/// <summary>An Active Setup "Installed Components" StubPath that runs once per user at logon.</summary>
public record ActiveSetupEntry(string Hive, string Component, string StubPath, bool TargetExists, bool Trusted, string? Signer);

/// <summary>Per-hive browser proxy configuration (proxy/PAC hijack check).</summary>
public record BrowserProxyEntry(string Hive, bool ProxyEnable, string? ProxyServer, string? AutoConfigUrl);

/// <summary>A site/domain mapped into a non-default IE/Edge security zone (e.g. Trusted, Local).</summary>
public record TrustedZoneEntry(string Hive, string Domain, string Zone);

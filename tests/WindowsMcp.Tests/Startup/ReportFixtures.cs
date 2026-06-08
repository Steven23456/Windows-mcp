using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Tests.Startup;

/// <summary>Builds a <see cref="StartupReportDto"/> with all-empty sections, overridable as needed.</summary>
internal static class ReportFixtures
{
    public static StartupReportDto Empty(
        StartupHeader? header = null,
        ProcessEntry[]? processes = null,
        RunEntry[]? run = null,
        string[]? errors = null) =>
        new(
            header ?? new StartupHeader("ZBOOK", "Windows 11", true,
                new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc), "Normal", "ZBOOK\\danie", null),
            processes ?? Array.Empty<ProcessEntry>(),
            run ?? Array.Empty<RunEntry>(),
            Array.Empty<StartupFolderEntry>(),
            Array.Empty<StartupTaskEntry>(),
            Array.Empty<StartupServiceEntry>(),
            Array.Empty<HostsEntry>(),
            Array.Empty<DnsEntry>(),
            Array.Empty<LspProviderEntry>(),
            Array.Empty<ShellExtensionEntry>(),
            Array.Empty<ControlPanelAppletEntry>(),
            Array.Empty<AccessibilityToolEntry>(),
            Array.Empty<IfeoEntry>(),
            Array.Empty<WinlogonHookEntry>(),
            Array.Empty<AppInitDllEntry>(),
            Array.Empty<ActiveSetupEntry>(),
            Array.Empty<BrowserProxyEntry>(),
            Array.Empty<TrustedZoneEntry>(),
            errors ?? Array.Empty<string>());
}

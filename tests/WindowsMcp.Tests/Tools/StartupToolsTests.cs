using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class StartupToolsTests
{
    [Fact]
    public async Task StartupReport_returns_json_then_text_rendering()
    {
        var dto = new StartupReportDto(
            new StartupHeader("ZBOOK", "Windows 11", true, new DateTime(2026, 6, 7, 0, 0, 0, DateTimeKind.Utc)),
            Processes: new[] { new ProcessEntry(1, "p", "C:\\p.exe", 5, true, null) },
            RunEntries: Array.Empty<RunEntry>(),
            StartupFolders: Array.Empty<StartupFolderEntry>(),
            ScheduledTasks: Array.Empty<StartupTaskEntry>(),
            Services: Array.Empty<StartupServiceEntry>(),
            Hosts: Array.Empty<HostsEntry>(),
            Lsp: Array.Empty<LspProviderEntry>(),
            ShellExtensions: Array.Empty<ShellExtensionEntry>(),
            Errors: Array.Empty<string>());

        var mock = new Mock<IStartupReportService>();
        mock.Setup(x => x.BuildAsync(It.IsAny<CancellationToken>())).ReturnsAsync(dto);

        var result = await new StartupTools(mock.Object).StartupReport();

        result.Should().Contain("\"Header\"");                  // structured JSON
        result.Should().Contain("Windows-mcp Startup Report");  // text rendering
        result.Should().Contain("== Processes (1) ==");
        mock.Verify(x => x.BuildAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}

using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tests.Startup;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class StartupToolsTests
{
    private static (StartupTools tools, Mock<IStartupReportService> mock) Make()
    {
        var dto = ReportFixtures.Empty(processes: new[] { new ProcessEntry(1, "p", "C:\\p.exe", 5, true, null) });
        var mock = new Mock<IStartupReportService>();
        mock.Setup(x => x.BuildAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(dto);
        return (new StartupTools(mock.Object), mock);
    }

    [Fact]
    public async Task StartupReport_defaults_to_json_only()
    {
        var (tools, _) = Make();
        var result = await tools.StartupReport();

        result.Should().Contain("\"Header\"");                       // structured JSON
        result.Should().NotContain("Windows-mcp Startup Report");    // no text rendering by default
    }

    [Fact]
    public async Task StartupReport_text_format_renders_human_readable()
    {
        var (tools, _) = Make();
        var result = await tools.StartupReport(format: "text");

        result.Should().Contain("Windows-mcp Startup Report");
        result.Should().Contain("== DNS servers (0) ==");           // a new section appears
        result.Should().NotContain("\"Header\"");
    }

    [Fact]
    public async Task StartupReport_both_format_includes_json_and_text()
    {
        var (tools, _) = Make();
        var result = await tools.StartupReport(format: "both");

        result.Should().Contain("\"Header\"").And.Contain("Windows-mcp Startup Report");
    }

    [Fact]
    public async Task StartupReport_passes_includeProcesses_through()
    {
        var (tools, mock) = Make();
        await tools.StartupReport(includeProcesses: true);

        mock.Verify(x => x.BuildAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartupReport_rejects_unknown_format()
    {
        var (tools, _) = Make();
        Func<Task> act = () => tools.StartupReport(format: "xml");
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*format*");
    }
}

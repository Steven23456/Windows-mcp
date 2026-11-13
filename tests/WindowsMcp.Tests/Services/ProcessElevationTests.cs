using System.Security.Principal;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-8: <c>ProcessElevation.IsElevated()</c> is THE elevation check - StartupReportService's former
/// private <c>IsElevated</c> (<c>WindowsPrincipal.IsInRole(Administrator)</c>) moved to one shared
/// internal helper that startup_report and the session probe both use. startup_report's output
/// does not change.
/// </summary>
/// <remarks>
/// "Both use it" cannot be observed through behaviour alone: two copies of the same check return
/// the same boolean. So the sharing is pinned at source level (the precedent is
/// <c>WindowsMcpHostTests.Program_does_not_write_the_plain_HTTP_warning_straight_to_stderr</c>),
/// and the value is pinned against the check it replaced.
/// </remarks>
[Trait("Category", "Unit")]
public class ProcessElevationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Windows-mcp.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the guard reads src/, so it must run from inside the repo");
        return dir!.FullName;
    }

    private static string Source(params string[] relative)
        => File.ReadAllText(Path.Combine([RepoRoot(), "src", "WindowsMcp", .. relative]));

    [Fact]
    public void StartupReportService_uses_the_shared_check_and_keeps_no_copy_of_its_own()
    {
        var source = Source("Services", "StartupReportService.cs");

        source.Should().Contain("ProcessElevation.IsElevated()", "the header's Elevated comes from the shared helper");
        source.Should().NotContain("WindowsBuiltInRole.Administrator", "the private copy is gone, not duplicated");
        Regex.IsMatch(source, @"\bbool\s+IsElevated\s*\(").Should().BeFalse("no private IsElevated remains beside the shared one");
    }

    [Fact]
    public void The_administrator_role_check_lives_in_ProcessElevation_only()
    {
        // One check, one place: any other file under src/WindowsMcp that asks IsInRole(Administrator)
        // is a second copy that can drift.
        var root = Path.Combine(RepoRoot(), "src", "WindowsMcp");
        var copies = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(f => File.ReadAllText(f).Contains("WindowsBuiltInRole.Administrator", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        copies.Should().Equal(["ProcessElevation.cs"]);
    }

    [Fact]
    public async Task Startup_report_header_reports_the_shared_checks_answer()
    {
        // startup_report's Elevated is unchanged: it is the same answer the shared helper gives.
        var report = await NewStartupReportService().BuildAsync();

        report.Header.Elevated.Should().Be(ProcessElevation.IsElevated());
    }

    /// <summary>The StartupReportServiceTests fakes, reduced to "every section empty".</summary>
    private static StartupReportService NewStartupReportService()
    {
        var process = new Mock<IProcessService>();
        process.Setup(x => x.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProcessDto>());
        var registry = new Mock<IRegistryService>();
        registry.Setup(x => x.EnumerateValuesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RegistryValueDto>());
        registry.Setup(x => x.EnumerateSubKeysAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        registry.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryValueDto("p", "ImagePath", null, "String"));
        var services = new Mock<IServiceControlService>();
        services.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ServiceDto>());
        var tasks = new Mock<ITaskSchedulerService>();
        tasks.Setup(x => x.ListDetailedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScheduledTaskDetailDto>());
        var fs = new Mock<IFileSystemService>();
        fs.Setup(x => x.ReadTextAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");
        fs.Setup(x => x.ListAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileListing(Array.Empty<FileEntry>(), false, int.MaxValue));
        var lsp = new Mock<ILspEnumerator>();
        lsp.Setup(x => x.Enumerate()).Returns(Array.Empty<LspProviderDto>());
        var auth = new Mock<IAuthenticodeInspector>();
        auth.Setup(x => x.Inspect(It.IsAny<string?>())).Returns(new AuthenticodeInfo(false, null));
        var shortcuts = new Mock<IShortcutResolver>();
        shortcuts.Setup(x => x.ResolveTarget(It.IsAny<string>())).Returns<string>(p => p);

        return new StartupReportService(process.Object, registry.Object, services.Object, tasks.Object,
            fs.Object, lsp.Object, auth.Object, shortcuts.Object);
    }
}

/// <summary>E-8: the shared check against the real token of the test process. Read-only.</summary>
[Trait("Category", "Integration")]
public class ProcessElevationIntegrationTests
{
    [Fact]
    public void IsElevated_is_the_WindowsPrincipal_administrator_check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        ProcessElevation.IsElevated().Should().Be(expected,
            "the check StartupReportService had privately, unchanged, so startup_report's Elevated does not change");
    }
}

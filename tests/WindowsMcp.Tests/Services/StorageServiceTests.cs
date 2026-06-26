using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class StorageServiceTests
{
    private static PSResult Ok(string stdout) => new(true, stdout, "", 0, Array.Empty<string>());
    private static StorageService Make(Mock<IPowerShellService> ps) => new(ps.Object);

    [Fact]
    public void BuildScript_sets_includeUsage_false_by_default()
        => StorageService.BuildScript(null, includeUsage: false).Should().Contain("$includeUsage = $false");

    [Fact]
    public void BuildScript_sets_includeUsage_true_when_requested()
        => StorageService.BuildScript(null, includeUsage: true).Should().Contain("$includeUsage = $true");

    [Fact]
    public void BuildScript_scopes_and_normalizes_drive_letter()
        => StorageService.BuildScript("f:", includeUsage: false).Should().Contain("$_.DriveLetter -eq 'F'");

    [Fact]
    public void BuildScript_has_no_drive_filter_when_letter_omitted()
        => StorageService.BuildScript(null, includeUsage: false).Should().NotContain("-eq '");

    [Fact]
    public void BuildScript_gates_physical_disks_and_smart_behind_includeUsage()
    {
        var s = StorageService.BuildScript(null, includeUsage: false);
        s.Should().Contain("if ($includeUsage)");          // physical-disk/SMART section is gated
        s.Should().Contain("Get-PhysicalDisk");            // present in the script, but behind the gate
        s.Should().Contain("Get-Disk | Sort-Object");      // fast default path (disks) is always present
        s.Should().Contain("busType");                     // disks carry bus type for the fast path
    }

    [Fact]
    public async Task GetHealthAsync_parses_report_json()
    {
        const string json = """
        {"physicalDisks":[{"deviceId":4,"friendlyName":"Seagate Portable","mediaType":"Unspecified","busType":"USB","sizeGb":4657.5,"healthStatus":"Healthy","operationalStatus":"OK","reliability":{"temperature":36,"readErrorsUncorrected":0,"writeErrorsUncorrected":null,"wear":0,"powerOnHours":12086}}],
         "disks":[{"number":4,"friendlyName":"Seagate Portable","operationalStatus":"Online","healthStatus":"Healthy","isOffline":false,"partitionStyle":"GPT","sizeGb":4657.5}],
         "volumes":[{"driveLetter":"F","fileSystemLabel":"Seagate Portable Drive","fileSystem":"NTFS","healthStatus":"Healthy","operationalStatus":"OK","sizeGb":4657.5,"freeGb":null,"diskNumber":4,"partitionNumber":2,"usageProbed":false,"usageTimedOut":false}],
         "recentEvents":[],"notes":[]}
        """;
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(json));

        var report = await Make(ps).GetHealthAsync();

        report.PhysicalDisks.Should().ContainSingle();
        report.PhysicalDisks[0].Reliability!.PowerOnHours.Should().Be(12086);
        report.Volumes.Should().ContainSingle();
        report.Volumes[0].DriveLetter.Should().Be("F");
        report.Volumes[0].DiskNumber.Should().Be(4);
        report.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHealthAsync_returns_note_when_output_empty()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Ok(""));

        var report = await Make(ps).GetHealthAsync();

        report.Notes.Should().NotBeEmpty();
        report.Disks.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHealthAsync_returns_budget_note_when_query_aborted()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(p => p.RunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
          .ThrowsAsync(new OperationCanceledException());

        // Caller's ct is NOT cancelled, so an OCE means our internal timeout fired.
        var report = await Make(ps).GetHealthAsync();

        report.Notes.Should().ContainMatch("*budget*");
    }
}

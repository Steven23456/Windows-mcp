using System.Collections.Generic;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Unit")]
public class ReliabilityServiceTests
{
    [Fact]
    public async Task GetAsync_maps_reliability_records_from_wmi()
    {
        var wmi = new Mock<IWmiService>();
        wmi.Setup(w => w.QueryAsync("Win32_ReliabilityRecords", null, null, It.IsAny<CancellationToken>()))
           .ReturnsAsync(new object[]
           {
               new Dictionary<string, object>
               {
                   ["SourceName"] = "Application Error",
                   ["Message"] = "app.exe stopped working",
                   ["EventIdentifier"] = 1000u,
               },
           });

        var report = await new ReliabilityService(wmi.Object).GetAsync();

        report.RecentFailures.Should().ContainSingle();
        report.RecentFailures[0].SourceName.Should().Be("Application Error");
        report.RecentFailures[0].EventId.Should().Be(1000);
        report.Note.Should().BeNull();
        report.Minidumps.Should().NotBeNull();
    }

    [Fact]
    public async Task GetAsync_sets_note_when_wmi_fails()
    {
        var wmi = new Mock<IWmiService>();
        wmi.Setup(w => w.QueryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("WMI down"));

        var report = await new ReliabilityService(wmi.Object).GetAsync();

        report.RecentFailures.Should().BeEmpty();
        report.Note.Should().Contain("unavailable");
        report.Minidumps.Should().NotBeNull(); // minidump scan is independent of WMI
    }

    [Fact]
    public async Task GetAsync_respects_the_record_cap()
    {
        var rows = Enumerable.Range(0, 100)
            .Select(i => (object)new Dictionary<string, object> { ["SourceName"] = $"S{i}" })
            .ToArray();
        var wmi = new Mock<IWmiService>();
        wmi.Setup(w => w.QueryAsync(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>()))
           .ReturnsAsync(rows);

        var report = await new ReliabilityService(wmi.Object).GetAsync(maxRecords: 10);

        report.RecentFailures.Should().HaveCount(10);
    }
}

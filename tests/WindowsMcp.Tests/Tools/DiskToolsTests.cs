using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class DiskToolsTests
{
    [Fact]
    public async Task DiskInspect_rejects_unknown_mode()
    {
        var tools = new DiskTools(new Mock<IDiskService>().Object);

        Func<Task> act = () => tools.DiskInspect("bogus_mode", @"C:\");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mode*");
    }

    [Fact]
    public async Task DiskInspect_usage_serializes_concrete_entries_not_empty_objects()
    {
        var disk = new Mock<IDiskService>();
        disk.Setup(d => d.GetUsageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new DiskUsageEntry(@"C:\Windows", 2048, "2.0 KB") });
        var tools = new DiskTools(disk.Object);

        var json = await tools.DiskInspect("usage", @"C:\");

        // Regression guard for the JsonSerializer.Serialize(object) -> "{}" trap.
        json.Should().Contain("Windows").And.Contain("2048");
    }
}

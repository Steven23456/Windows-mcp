using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class StorageToolsTests
{
    private static StorageHealthReport Sample() =>
        new(Array.Empty<PhysicalDiskInfo>(), Array.Empty<DiskInfo>(),
            new[] { new VolumeInfo("F", "Seagate", "NTFS", "Healthy", "OK", 4657.5, null, 4, 2, false, false) },
            Array.Empty<DiskEventInfo>(), Array.Empty<string>());

    [Theory]
    [InlineData("ZZ")]
    [InlineData("5")]
    [InlineData("F::")]
    public async Task StorageHealth_rejects_invalid_drive_letter(string bad)
    {
        var tools = new StorageTools(new Mock<IStorageService>().Object);

        Func<Task> act = () => tools.StorageHealth(bad);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*drive_letter*");
    }

    [Fact]
    public async Task StorageHealth_returns_serialized_report()
    {
        var svc = new Mock<IStorageService>();
        svc.Setup(s => s.GetHealthAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Sample());

        var json = await new StorageTools(svc.Object).StorageHealth("F");

        json.Should().Contain("\"DriveLetter\":\"F\"");
    }

    [Fact]
    public async Task StorageHealth_passes_arguments_through_to_service()
    {
        var svc = new Mock<IStorageService>();
        svc.Setup(s => s.GetHealthAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
           .ReturnsAsync(Sample());

        await new StorageTools(svc.Object).StorageHealth("F:", include_usage: true, timeout_seconds: 12);

        svc.Verify(s => s.GetHealthAsync("F:", true, 12, It.IsAny<CancellationToken>()), Times.Once);
    }
}

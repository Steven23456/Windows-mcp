using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class FileToolsTests
{
    private static FileTools MakeTools(
        IFileSystemService? fs = null,
        IInputService? input = null)
    {
        return new FileTools(
            fs    ?? new Mock<IFileSystemService>().Object,
            input ?? new Mock<IInputService>().Object);
    }

    [Fact]
    public async Task FileWrite_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        Func<Task> act = () => tools.FileWrite(@"C:\tmp\file.txt", "hello", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.WriteTextAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FileManage_delete_requires_confirm()
    {
        var mock = new Mock<IFileSystemService>();
        var tools = MakeTools(fs: mock.Object);

        Func<Task> act = () => tools.FileManage("delete", @"C:\tmp\file.txt", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mock.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FileSearch_passes_args_to_service()
    {
        var mock = new Mock<IFileSystemService>();
        var isoDate = "2024-01-15T10:00:00Z";
        var expectedDate = DateTime.Parse(isoDate, null, System.Globalization.DateTimeStyles.RoundtripKind);

        mock.Setup(s => s.SearchAsync(
                @"C:\data", "*.txt", null, It.Is<DateTime?>(d => d.HasValue && d.Value == expectedDate), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileSearchHit>());

        var tools = MakeTools(fs: mock.Object);
        var result = await tools.FileSearch(@"C:\data", "*.txt", modified_since: isoDate);

        result.Should().NotBeNull();
        mock.VerifyAll();
    }
}

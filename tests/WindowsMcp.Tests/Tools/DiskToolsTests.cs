using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class DiskToolsTests
{
    [Fact]
    public async Task DiskInspect_rejects_unknown_mode()
    {
        var tools = new DiskTools(
            new Mock<IFileSystemService>().Object,
            new Mock<IPowerShellService>().Object);

        Func<Task> act = () => tools.DiskInspect("bogus_mode", @"C:\");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mode*");
    }
}

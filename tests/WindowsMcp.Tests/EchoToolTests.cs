using FluentAssertions;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests;

[Trait("Category", "Unit")]
public class EchoToolTests
{
    [Fact]
    public void Echo_returns_the_input_text()
    {
        var result = EchoTool.Echo("hello windows-mcp");
        result.Should().Be("hello windows-mcp");
    }
}

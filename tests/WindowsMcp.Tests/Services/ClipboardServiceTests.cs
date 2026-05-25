using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "Integration")]
public class ClipboardServiceTests : IDisposable
{
    private readonly ClipboardService _svc = new();
    private readonly string? _saved;

    public ClipboardServiceTests()
    {
        _saved = _svc.GetTextAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task SetTextAsync_then_GetTextAsync_roundtrips()
    {
        await _svc.SetTextAsync("hello windows-mcp test");
        var got = await _svc.GetTextAsync();
        got.Should().Be("hello windows-mcp test");
    }

    public void Dispose()
    {
        if (_saved is not null) _svc.SetTextAsync(_saved).GetAwaiter().GetResult();
    }
}

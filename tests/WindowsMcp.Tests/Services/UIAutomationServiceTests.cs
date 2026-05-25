using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

[Trait("Category", "UIAutomation")]
public class UIAutomationServiceTests : IClassFixture<NotepadFixture>
{
    private readonly NotepadFixture _np;

    public UIAutomationServiceTests(NotepadFixture np) => _np = np;

    [Fact]
    public async Task GetStateAsync_returns_tree_with_notepad_root()
    {
        using var svc = new UIAutomationService();
        var state = await svc.GetStateAsync();
        state.Root.Name.Should().NotBeNullOrEmpty();
        state.Children.Should().NotBeEmpty();
    }

    [Fact]
    public async Task FindElementAsync_finds_notepad_text_area()
    {
        using var svc = new UIAutomationService();
        var matches = await svc.FindElementAsync("", FindKind.Text);
        matches.Matches.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Concurrency_50_parallel_calls_no_deadlock()
    {
        using var svc = new UIAutomationService();
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => svc.GetStateAsync()).ToArray();
        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(r => r.Root.Should().NotBeNull());
    }
}

// Separate class so it doesn't need the NotepadFixture — Dispose tears down before any
// UIA call, so this test does not need a live desktop session and is Unit-trait safe.
[Trait("Category", "Unit")]
public class UIAutomationServiceUnitTests
{
    [Fact]
    public async Task GetStateAsync_throws_after_dispose()
    {
        var svc = new UIAutomationService();
        svc.Dispose();
        Func<Task> act = () => svc.GetStateAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}

using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

public class InputServiceTests
{
    // Recategorized to Integration: SendInput fails under test runner (UIPI elevation mismatch).
    // The service logic is correct; real desktop session required for these tests.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClickAsync_returns_result_with_correct_coordinates_and_button()
    {
        var service = new InputService();
        var result = await service.ClickAsync(100, 200, MouseButton.Left);
        result.Should().BeEquivalentTo(new ClickResult(100, 200, MouseButton.Left, 1));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TypeAsync_reports_character_count_for_unicode_input()
    {
        var service = new InputService();
        var result = await service.TypeAsync("héllo");
        result.CharsTyped.Should().Be(5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PressShortcutAsync_throws_on_invalid_format()
    {
        var service = new InputService();
        Func<Task> act = () => service.PressShortcutAsync("not+a+real+key");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}

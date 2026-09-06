using FluentAssertions;
using WindowsInput;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-7 (R107-R111): the held-key pair <c>multi_select</c> needs. Driven against the recording
/// sink, so nothing is injected and the class stays Unit (roadmap C10) — what it pins is that the
/// service passes the key name through to the sink untouched and in the caller's order. The real
/// H.InputSimulator mapping (ShortcutParser.ResolveKey → KeyDown/KeyUp, as
/// <c>ScrollAsync(shiftWheel)</c> already does for Shift) can only be observed on a live desktop:
/// <c>InputToolsBatchDesktopTests</c> is where a modifier left stuck would show up.
/// </summary>
[Trait("Category", "Unit")]
public class InputServiceKeyHoldTests
{
    private sealed class Recorder : IKeyboardSink
    {
        public List<string> Log { get; } = [];

        public void Shortcut(string chord) => Log.Add($"shortcut:{chord}");
        public void Key(string key) => Log.Add($"key:{key}");
        public void Text(string text) => Log.Add($"text:{text}");
        public void KeyDown(string key) => Log.Add($"down:{key}");
        public void KeyUp(string key) => Log.Add($"up:{key}");
    }

    private static (InputService Service, Recorder Sink) NewService()
    {
        var recorder = new Recorder();
        return (new InputService(null, recorder), recorder);
    }

    [Theory]
    [InlineData("ctrl")]
    [InlineData("shift")]
    [InlineData("alt")]
    public async Task KeyDownAsync_holds_the_key_the_caller_named(string key)
    {
        var (service, sink) = NewService();

        await service.KeyDownAsync(key);

        // The array form, not the params form: with params the reason would be read as a second
        // EXPECTED element (see FluentAssertionsUsageTests).
        sink.Log.Should().Equal(new[] { $"down:{key}" },
            "the sink resolves the name; the service must not rewrite it");
    }

    [Theory]
    [InlineData("ctrl", VirtualKeyCode.CONTROL)]
    [InlineData("shift", VirtualKeyCode.SHIFT)]
    [InlineData("alt", VirtualKeyCode.MENU)]
    public void The_names_a_batch_holds_down_resolve_to_a_modifier_key(string key, VirtualKeyCode expected)
    {
        // The recording sink above cannot see this: the REAL sink is
        // sim.Keyboard.KeyDown(ShortcutParser.ResolveKey(key).Key), so a name this table does not
        // know would throw on a live desktop while every mocked test stayed green (CLAUDE.md's
        // "a mocked collaborator is not evidence"). multi_select passes "ctrl".
        var token = ShortcutParser.ResolveKey(key);

        token.Key.Should().Be(expected);
        token.ImpliedModifiers.Should().BeEmpty("a modifier is held on its own, not through another one");
    }

    [Fact]
    public async Task KeyUpAsync_releases_the_key_the_caller_named()
    {
        var (service, sink) = NewService();

        await service.KeyUpAsync("ctrl");

        sink.Log.Should().Equal("up:ctrl");
    }

    [Fact]
    public async Task A_hold_is_a_bracket_around_whatever_happens_between()
    {
        var (service, sink) = NewService();

        await service.KeyDownAsync("ctrl");
        await service.PressKeyAsync("a");
        await service.KeyUpAsync("ctrl");

        sink.Log.Should().Equal("down:ctrl", "key:a", "up:ctrl");
    }

    [Fact]
    public async Task A_cancelled_token_sends_nothing()
    {
        // Consistent with every other verb on the service: the check happens before the sink, so a
        // cancelled batch cannot leave a modifier held down.
        var (service, sink) = NewService();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> down = () => service.KeyDownAsync("ctrl", cts.Token);
        Func<Task> up = () => service.KeyUpAsync("ctrl", cts.Token);

        await down.Should().ThrowAsync<OperationCanceledException>();
        await up.Should().ThrowAsync<OperationCanceledException>();
        sink.Log.Should().BeEmpty();
    }
}

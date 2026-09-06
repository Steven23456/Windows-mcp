using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

/// <summary>
/// B-7 (R107-R110) on a live desktop: the two batch tools driven through the REAL services
/// against the Notepad fixture. The mocked rows prove the order of what WOULD be injected; these
/// prove it lands — and, above all, that Ctrl comes back UP. A modifier left held down after a
/// failed batch makes every later keystroke on the machine a shortcut, which is the one failure
/// mode of this item that a user notices immediately and no mock can see.
/// <para>
/// One Notepad window, not two: the modern Notepad is a single process hosting every window and
/// the fixture identifies its own by an inventory diff (see <see cref="DesktopCollection"/>), so
/// the two-field case is two entries into the same editor.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class InputToolsBatchDesktopTests : IClassFixture<NotepadFixture>, IDisposable
{
    private const int VkControl = 0x11;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly UIAutomationService _uia;
    private readonly InputTools _tools;
    private readonly ClipboardService _clipboard;

    public InputToolsBatchDesktopTests(NotepadFixture np)
    {
        np.BringToForeground();
        _clipboard = new ClipboardService();
        _uia = new UIAutomationService(new InputService(_clipboard), new WindowService());
        _tools = new InputTools(new InputService(_clipboard), _clipboard, _uia);
    }

    public void Dispose()
    {
        _uia.Dispose();
        GC.SuppressFinalize(this);
    }

    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<ElementInfo> EditorAsync()
    {
        var found = await _uia.FindElementAsync("", FindKind.Text, FindScope.Window, "Notepad");
        return found.Matches.FirstOrDefault(m => m.ControlType is "Document" or "Edit")
            ?? throw new Xunit.Sdk.XunitException("No Document/Edit element in the Notepad window");
    }

    [Fact]
    public async Task Multi_edit_runs_both_entries_in_one_call()
    {
        var editor = await EditorAsync();
        var first = $"b7a{Guid.NewGuid():N}"[..10];
        var second = $"b7b{Guid.NewGuid():N}"[..10];

        var json = Json(await _tools.MultiEdit(
            $$"""[{"element_id":"{{editor.ElementId}}","text":"{{first}}","clear":true},{"element_id":"{{editor.ElementId}}","text":"{{second}}","clear":true}]"""));

        json.GetProperty("count").GetInt32().Should().Be(2);
        json.GetProperty("results").GetArrayLength().Should().Be(2);
        json.GetProperty("results")[1].GetProperty("ok").GetBoolean().Should().BeTrue();
        await Task.Delay(400);   // injected keystrokes are asynchronous to the UIA read
        var text = await _uia.GetTextAsync(editor.ElementId);
        text.Should().Contain(second, "the second entry ran too - one call, both fields");
        text.Should().NotContain(first, "clear:true on the second entry replaced the first entry's text");
    }

    [Fact]
    public async Task Multi_select_with_ctrl_leaves_no_modifier_stuck_down()
    {
        // The real risk of the item. Ctrl is pressed with keybd_event/SendInput, which changes
        // GLOBAL keyboard state: if the release is skipped, every keystroke afterwards - in every
        // application, including the test runner's - becomes a chord.
        var editor = await EditorAsync();
        var bounds = editor.Bounds!;
        int x = bounds.X + bounds.Width / 3;
        int y = bounds.Y + bounds.Height / 3;

        var json = Json(await _tools.MultiSelect(
            $$"""[{"x":{{x}},"y":{{y}}},{"x":{{x + 20}},"y":{{y}}}]"""));

        json.GetProperty("count").GetInt32().Should().Be(2);
        json.GetProperty("ctrl").GetBoolean().Should().BeTrue();
        await Task.Delay(200);
        (GetAsyncKeyState(VkControl) & 0x8000).Should().Be(0,
            "multi_select releases Ctrl in a finally; a stuck modifier would make every later keystroke a chord");
    }

    [Fact]
    public async Task Multi_select_releases_ctrl_even_when_the_batch_fails_midway()
    {
        // Entry 2 aims at a coordinate that is on no monitor: the click throws inside the batch,
        // after Ctrl went down. The result reports how far it got, and Ctrl is still released.
        var editor = await EditorAsync();
        var bounds = editor.Bounds!;
        int x = bounds.X + bounds.Width / 3;
        int y = bounds.Y + bounds.Height / 3;

        var json = Json(await _tools.MultiSelect(
            $$"""[{"x":{{x}},"y":{{y}}},{"x":-32000,"y":-32000}]"""));

        json.GetProperty("failedIndex").GetInt32().Should().Be(1);
        json.GetProperty("results").GetArrayLength().Should().Be(1);
        await Task.Delay(200);
        (GetAsyncKeyState(VkControl) & 0x8000).Should().Be(0, "the finally runs on the failure path too");
    }
}

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-8's "done when" on a live desktop: <c>launch("calc")</c> and <c>launch("notepad")</c> really
/// open, through the real catalog and the real activation manager, and come back with a handle
/// <c>window list</c> can see. Everything else in B-8 is mocked somewhere — this is the only
/// place a broken <c>IApplicationActivationManager</c> declaration or an empty Start Menu scan
/// shows up.
/// <para>
/// <c>Category=UIAutomation</c> and <see cref="DesktopCollection"/>: launching an app puts a new
/// window in front of whatever the user is doing, and a Notepad window must never appear while
/// another class is holding a <see cref="NotepadFixture"/> (modern Notepad is one process and the
/// fixtures identify their windows by an inventory diff). Never run unattended.
/// </para>
/// <para>
/// The two heavy apps the checklist also names — Microsoft Edge and Visual Studio Code — are
/// deliberately NOT launched here: they are pinned as catalog facts in
/// <see cref="AppCatalogServiceIntegrationTests"/> instead, which is the part that can go wrong.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
[Collection(DesktopCollection.Name)]
public class LaunchDesktopTests
{
    private static WindowTools NewTools()
        => new(new WindowService(null, new AppCatalogService()), new Mock<IVirtualDesktopService>().Object);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<HashSet<long>> HandlesAsync()
        => (await new WindowService().ListAsync(includeMinimized: true)).Select(w => w.Hwnd).ToHashSet();

    /// <summary>
    /// Close the window this test opened, and only that one: WM_CLOSE through the same service,
    /// then a kill of the reported pid if the window is still there. Best effort — a failure to
    /// clean up must not turn the assertion red, it must be visible in the run log.
    /// </summary>
    private static async Task CloseAsync(long hwnd, int pid)
    {
        try { await new WindowService().ExecuteAsync("close", null, hwnd); }
        catch (Exception ex) { Console.Error.WriteLine($"[LaunchDesktopTests] WM_CLOSE to {hwnd:X} failed: {ex.Message}"); }

        for (int i = 0; i < 30; i++)
        {
            if (!(await HandlesAsync()).Contains(hwnd)) return;
            await Task.Delay(100);
        }

        try { Process.GetProcessById(pid).Kill(entireProcessTree: false); }
        catch (Exception ex) { Console.Error.WriteLine($"[LaunchDesktopTests] kill {pid} failed: {ex.Message}"); }
    }

    [Fact]
    public async Task Launch_calc_opens_Calculator_and_reports_a_window_the_inventory_shows()
    {
        var before = await HandlesAsync();

        var root = Parse(await NewTools().Launch("calc"));

        root.GetProperty("MatchedName").GetString().Should().Be("Calculator");
        root.GetProperty("Kind").GetString().Should().Be("packaged");
        root.GetProperty("WindowDetected").GetBoolean().Should().BeTrue(
            "the whole point of the wait is that launch comes back with a handle to act on");
        long hwnd = root.GetProperty("Hwnd").GetInt64();
        int pid = root.GetProperty("Pid").GetInt32();
        try
        {
            hwnd.Should().NotBe(0);
            pid.Should().BeGreaterThan(0, "ActivateApplication is the route that hands back a pid");
            var listed = (await new WindowService().ListAsync()).SingleOrDefault(w => w.Hwnd == hwnd);
            listed.Should().NotBeNull("the handle launch reported has to be one window list shows");
            listed!.Title.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            if (!before.Contains(hwnd)) await CloseAsync(hwnd, pid);
        }
    }

    [Fact]
    public async Task Launch_notepad_opens_a_Notepad_window_and_reports_it()
    {
        // Notepad's tab state is persisted and restored on the next start, so a window opened and
        // not cleaned up comes back for days (NotepadFixture's second documented fact). The sweep
        // below is the same one the fixture does.
        var tabsBefore = NotepadFixture.TabStateFiles(NotepadFixture.TabStateDirectory);
        var before = await HandlesAsync();

        var root = Parse(await NewTools().Launch("notepad"));

        root.GetProperty("MatchedName").GetString().Should().Be("Notepad");
        root.GetProperty("Kind").GetString().Should().Be("packaged",
            "modern Notepad is an MSIX app; the catalog resolves it through the package manager");
        root.GetProperty("WindowDetected").GetBoolean().Should().BeTrue();
        long hwnd = root.GetProperty("Hwnd").GetInt64();
        int pid = root.GetProperty("Pid").GetInt32();
        try
        {
            root.GetProperty("Title").GetString().Should().Contain("Notepad",
                "the window the wait picked has to be a Notepad window, not whatever appeared next");
            (await new WindowService().ListAsync()).Should().Contain(w => w.Hwnd == hwnd);
        }
        finally
        {
            if (!before.Contains(hwnd)) await CloseAsync(hwnd, pid);
            foreach (var name in NotepadFixture.NewTabStateFiles(
                         tabsBefore, NotepadFixture.TabStateFiles(NotepadFixture.TabStateDirectory)))
            {
                try { File.Delete(Path.Combine(NotepadFixture.TabStateDirectory, name)); }
                catch (Exception ex) { Console.Error.WriteLine($"[LaunchDesktopTests] tab sweep: {ex.Message}"); }
            }
        }
    }

    [Fact]
    public async Task Launch_of_a_name_this_machine_does_not_have_lists_the_nearest_apps()
    {
        // The miss path against the real catalog: nothing is started, and the message is the five
        // nearest names the model can retry with.
        var act = () => NewTools().Launch("zzqq-not-an-app-zzqq");

        var message = (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;
        message.Should().Contain("zzqq-not-an-app-zzqq");
    }
}

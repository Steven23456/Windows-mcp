using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-8: what <c>launch</c> decides before anything starts — is this a path or a name, is the
/// resolved entry packaged or a shortcut, which of the two activation routes gets called, and
/// what the result reports. Driven through the constructor seam with a fake catalog and a
/// recording activator, so no process is ever started and no window is ever opened.
/// <para>
/// The catalog rules are <see cref="AppCatalogTests"/>'s, the window wait is
/// <see cref="LaunchWaitTests"/>'s, and the two routes really working is the desktop bracket's
/// (<c>LaunchDesktopTests</c>). Every test here passes <c>waitForWindow:false</c> so the
/// inventory is never read.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class WindowServiceLaunchTests
{
    /// <summary>Records which route was taken and hands back a fixed pid.</summary>
    private sealed class RecordingActivator : IAppActivator
    {
        public List<string> Calls { get; } = [];
        public int Pid { get; init; } = 4242;

        public int ActivatePackaged(string aumid)
        {
            Calls.Add("packaged:" + aumid);
            return Pid;
        }

        public int StartShortcutOrPath(string target)
        {
            Calls.Add("shell:" + target);
            return Pid;
        }
    }

    private static Mock<IAppCatalogService> Catalog(AppEntry entry, int score = 100, string strategy = "exact")
    {
        var mock = new Mock<IAppCatalogService>();
        mock.Setup(c => c.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AppMatch(entry, score, strategy));
        return mock;
    }

    private static WindowService Service(IAppCatalogService catalog, IAppActivator activator)
        => new(null, null, catalog, activator);

    private static readonly AppEntry CalculatorPackage =
        new("Calculator", "packaged", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "package:Microsoft.WindowsCalculator_8wekyb3d8bbwe");

    private static readonly AppEntry EdgeShortcut =
        new("Microsoft Edge", "shortcut",
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Microsoft Edge.lnk",
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs");

    // ---- the path short-circuit ---------------------------------------------------------------

    [Fact]
    public async Task LaunchAsync_starts_an_existing_path_without_consulting_the_catalog()
    {
        // launch(@"C:\...\thing.exe") is not a fuzzy question. Sending it through the catalog
        // would let a Start Menu entry hijack a path the caller spelled out in full.
        var file = Path.Combine(Path.GetTempPath(), "wmcp-launch-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, "x");
        try
        {
            var catalog = new Mock<IAppCatalogService>(MockBehavior.Strict);
            var activator = new RecordingActivator();

            var result = await Service(catalog.Object, activator).LaunchAsync(file, waitForWindow: false, timeoutMs: 10_000);

            result.Strategy.Should().Be("path");
            result.Kind.Should().Be("path");
            result.MatchedName.Should().Be(file, "there is no catalog entry to name, so the path is the name");
            result.Score.Should().Be(100);
            result.Pid.Should().Be(4242);
            activator.Calls.Should().Equal("shell:" + file);
            catalog.VerifyNoOtherCalls();
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task LaunchAsync_starts_an_executable_name_that_resolves_on_PATH_without_the_catalog()
    {
        // "notepad.exe" is on PATH on every Windows box; today's launch(app_name) already works
        // this way and B-8 must not break it.
        var catalog = new Mock<IAppCatalogService>(MockBehavior.Strict);
        var activator = new RecordingActivator();

        var result = await Service(catalog.Object, activator).LaunchAsync("notepad.exe", false, 10_000);

        result.Strategy.Should().Be("path");
        result.Kind.Should().Be("path");
        result.MatchedName.Should().Be("notepad.exe");
        activator.Calls.Should().Equal("shell:notepad.exe");
        catalog.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LaunchAsync_sends_a_bare_name_to_the_catalog_not_to_ShellExecute()
    {
        // "calc" is not a file, so today's ShellExecute path would fail; the catalog is what makes
        // it work at all.
        var catalog = Catalog(CalculatorPackage, 100, "prefix");
        var activator = new RecordingActivator();

        await Service(catalog.Object, activator).LaunchAsync("calc", false, 10_000);

        catalog.Verify(c => c.ResolveAsync("calc", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- packaged versus shortcut -------------------------------------------------------------

    [Fact]
    public async Task LaunchAsync_activates_a_packaged_app_by_its_AUMID()
    {
        // Roadmap C7: the activation manager is the only route that gives back the PID; a
        // ShellExecute of an AUMID would return explorer's pid or nothing useful.
        var activator = new RecordingActivator { Pid = 1234 };

        var result = await Service(Catalog(CalculatorPackage, 100, "prefix").Object, activator)
            .LaunchAsync("calc", false, 10_000);

        activator.Calls.Should().Equal("packaged:Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        result.Pid.Should().Be(1234);
        result.Kind.Should().Be("packaged");
        result.MatchedName.Should().Be("Calculator", "the entry's name, not the string the caller typed");
        result.Strategy.Should().Be("prefix", "the matcher's verdict is passed through, not re-derived");
        result.Score.Should().Be(100);
    }

    [Fact]
    public async Task LaunchAsync_shell_executes_a_shortcut_entry_by_its_lnk_path()
    {
        var activator = new RecordingActivator();

        var result = await Service(Catalog(EdgeShortcut, 100, "fuzzy").Object, activator)
            .LaunchAsync("edge", false, 10_000);

        activator.Calls.Should().Equal(new[] { "shell:" + EdgeShortcut.Target },
            "ShellExecute opens a .lnk directly - the shortcut never has to be resolved to its target");
        result.Kind.Should().Be("shortcut");
        result.MatchedName.Should().Be("Microsoft Edge");
        result.Strategy.Should().Be("fuzzy");
    }

    [Fact]
    public async Task LaunchAsync_reports_the_fuzzy_score_the_catalog_gave_it()
    {
        var result = await Service(Catalog(EdgeShortcut, 73, "fuzzy").Object, new RecordingActivator())
            .LaunchAsync("edg", false, 10_000);

        result.Score.Should().Be(73, "the model is told how sure the match was");
    }

    [Fact]
    public async Task LaunchAsync_lets_a_catalog_miss_reach_the_caller()
    {
        // The five-nearest message is the whole value of a miss; swallowing it into
        // windowDetected:false would hide it.
        var catalog = new Mock<IAppCatalogService>();
        catalog.Setup(c => c.ResolveAsync("zzqq", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("No app matching 'zzqq'. Nearest: 'Calculator' (12)"));
        var activator = new RecordingActivator();

        var act = () => Service(catalog.Object, activator).LaunchAsync("zzqq", false, 10_000);

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("Nearest");
        activator.Calls.Should().BeEmpty("nothing is started when nothing matched");
    }

    // ---- the wait, and not waiting ------------------------------------------------------------

    [Fact]
    public async Task LaunchAsync_without_the_wait_reports_no_window_immediately()
    {
        var result = await Service(Catalog(CalculatorPackage).Object, new RecordingActivator())
            .LaunchAsync("calc", waitForWindow: false, timeoutMs: 60_000);

        result.WindowDetected.Should().BeFalse();
        result.Hwnd.Should().BeNull("no window was looked for, so none can be reported");
        result.Title.Should().BeNull();
    }

    // ---- refusals -----------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LaunchAsync_refuses_a_blank_app_name(string appName)
    {
        var catalog = new Mock<IAppCatalogService>(MockBehavior.Strict);
        var activator = new RecordingActivator();

        var act = () => Service(catalog.Object, activator).LaunchAsync(appName, false, 10_000);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("app");
        activator.Calls.Should().BeEmpty();
        catalog.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60_001)]
    public async Task LaunchAsync_refuses_a_timeout_outside_the_range(int timeoutMs)
    {
        var activator = new RecordingActivator();

        var act = () => Service(Catalog(CalculatorPackage).Object, activator).LaunchAsync("calc", true, timeoutMs);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message
            .Should().Contain("60000", "the model is told the range, not just that it was wrong");
        activator.Calls.Should().BeEmpty("a bad argument must not start an application");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60_000)]
    public async Task LaunchAsync_accepts_both_ends_of_the_timeout_range(int timeoutMs)
    {
        // waitForWindow:false so the accepted value costs nothing; the point is that it is not
        // rejected.
        var result = await Service(Catalog(CalculatorPackage).Object, new RecordingActivator())
            .LaunchAsync("calc", false, timeoutMs);

        result.Pid.Should().Be(4242);
    }

    [Fact]
    public async Task LaunchAsync_honours_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var activator = new RecordingActivator();

        var act = () => Service(Catalog(CalculatorPackage).Object, activator).LaunchAsync("calc", false, 10_000, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        activator.Calls.Should().BeEmpty();
    }

    [Fact]
    public void LaunchAsync_is_an_added_overload_not_a_replacement()
    {
        // The single-argument member is still on the interface, still returning a pid: B-8 adds
        // an overload rather than re-signing a member other callers use.
        var old = typeof(IWindowService).GetMethod(
            nameof(IWindowService.LaunchAsync), [typeof(string), typeof(CancellationToken)]);

        old.Should().NotBeNull("removing it would be a silent contract break for every other caller");
        old!.ReturnType.Should().Be(typeof(Task<int>));
    }

    // ---- what counts as a path, and what does not ----------------------------------------------

    [Fact]
    public async Task LaunchAsync_starts_an_existing_directory_as_a_path()
    {
        // ShellExecute on a folder opens Explorer at it - a legitimate thing to ask for, and not
        // something the Start Menu catalog could ever answer.
        var dir = Path.Combine(Path.GetTempPath(), "wmcp-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var catalog = new Mock<IAppCatalogService>(MockBehavior.Strict);
            var activator = new RecordingActivator();

            var result = await Service(catalog.Object, activator).LaunchAsync(dir, false, 10_000);

            result.Strategy.Should().Be("path");
            result.Kind.Should().Be("path");
            activator.Calls.Should().Equal(new[] { "shell:" + dir });
            catalog.VerifyNoOtherCalls();
        }
        finally
        {
            try { Directory.Delete(dir); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("calc")]
    [InlineData("notepad")]
    [InlineData("explorer")]
    public async Task LaunchAsync_sends_a_bare_word_to_the_catalog_even_when_that_exe_is_on_PATH(string appName)
    {
        // Regression: "calc" is a Start Menu name AND calc.exe exists in System32. Short-circuiting
        // it to ShellExecute launched the stub instead of the Calculator app the catalog resolves
        // to, and the result claimed Strategy "path". Only an explicit ".exe" is a path.
        var catalog = Catalog(CalculatorPackage, 100, "prefix");
        var activator = new RecordingActivator();

        var result = await Service(catalog.Object, activator).LaunchAsync(appName, false, 10_000);

        catalog.Verify(c => c.ResolveAsync(appName, It.IsAny<CancellationToken>()), Times.Once);
        result.Strategy.Should().Be("prefix", "the catalog's verdict, not 'path'");
        result.Kind.Should().Be("packaged");
        activator.Calls.Should().Equal(new[] { "packaged:" + CalculatorPackage.Target },
            "the AUMID the catalog gave, never the bare word");
    }

    [Theory]
    [InlineData("a<b")]                       // not a file, and not a path .NET will look at
    [InlineData("a\0b")]                     // a NUL: Path.GetInvalidPathChars() rejects it
    [InlineData("Visual Studio Code")]        // spaces, no separator, no extension
    [InlineData(@"C:\wmcp-no-such-dir\nothing-here.exe")]   // a path shape that does not exist
    public async Task LaunchAsync_sends_anything_that_is_not_an_existing_path_to_the_catalog(string appName)
    {
        var catalog = Catalog(EdgeShortcut, 100, "exact");
        var activator = new RecordingActivator();

        var result = await Service(catalog.Object, activator).LaunchAsync(appName, false, 10_000);

        catalog.Verify(c => c.ResolveAsync(appName, It.IsAny<CancellationToken>()), Times.Once,
            "an odd string is a name to look up, not a file to run and not a crash");
        result.Strategy.Should().Be("exact");
        activator.Calls.Should().Equal(new[] { "shell:" + EdgeShortcut.Target });
    }

    [Fact]
    public async Task LaunchAsync_refuses_a_name_when_no_catalog_was_wired_in()
    {
        // DI always supplies one (WindowsMcpHostTests pins that); a WindowService built without
        // one must say so rather than throwing a NullReferenceException at the model.
        var activator = new RecordingActivator();
        var service = new WindowService(null, null, null, activator);

        var act = () => service.LaunchAsync("calc", false, 10_000);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("calc").And.ContainEquivalentOf("catalog");
        activator.Calls.Should().BeEmpty("nothing is started when the name could not be resolved");
    }
}

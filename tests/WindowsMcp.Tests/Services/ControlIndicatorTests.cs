using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// S-11 (R7) through the REAL windows. <see cref="ControlIndicatorStateTests"/> proves the lifetime
/// rules and <see cref="ControlIndicatorArtTests"/> the pixels; both would stay green if no window
/// were ever created, if the hotkey were never registered, or if the pointer-follow tick never ran
/// the fade - the failure mode CLAUDE.md records for <c>disk_inspect mode:reclaimable</c>.
/// <para>
/// Categorised like <see cref="FlashOverlayTests"/>: it needs an interactive window station but no
/// foreground app, injects NO input (the takeover is a posted <c>WM_HOTKEY</c>, never a key press)
/// and reads no pixels, so it is <c>Category=Integration</c>. It does draw a ring at the pointer and
/// a banner at the top of the screen, and one test turns display affinity off, so it joins
/// <see cref="DesktopCollection"/> (the "draws on the desktop" rule) and never runs beside a class
/// that compares screen pixels.
/// </para>
/// <para>
/// Every indicator here uses Ctrl+Alt+Shift+F13, never the user's real Ctrl+Alt+Shift+Backspace:
/// no physical keyboard has F13 on most desks, and a test that registered the real combination
/// would take it from whatever else on the machine uses it. "Is the combination registered" is
/// asked the only way Win32 answers it - a second <c>RegisterHotKey</c> of the same chord fails
/// while the first holds it - and a probe that unexpectedly succeeds is released at once.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public class ControlIndicatorTests
{
    internal static readonly HotkeyChord TestChord =
        new(HotkeyChord.ModControl | HotkeyChord.ModAlt | HotkeyChord.ModShift, 0x7C, "Ctrl+Alt+Shift+F13");

    internal const uint WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ProbeId = 0x7E57;
    private const int HoldId = 0x7E58;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint affinity);

    private const uint WDA_NONE = 0x00, WDA_EXCLUDEFROMCAPTURE = 0x11;

    internal static ControlIndicatorOptions Options(bool enabled = true, int lingerMs = 300) =>
        new(enabled, TestChord, TimeSpan.FromMilliseconds(lingerMs));

    /// <summary>
    /// True when nobody holds the test chord: a probe registration succeeds (and is released at once,
    /// on the same thread - a thread-bound hotkey can only be unregistered by its own thread). Probed
    /// with and without MOD_NOREPEAT so it does not matter which form the indicator registered.
    /// </summary>
    internal static bool ChordIsFree()
    {
        foreach (var mods in new[] { TestChord.Modifiers, TestChord.Modifiers | MOD_NOREPEAT })
        {
            if (!RegisterHotKey(IntPtr.Zero, ProbeId, mods, TestChord.Key)) return false;
            UnregisterHotKey(IntPtr.Zero, ProbeId);
        }
        return true;
    }

    /// <summary>The takeover as Windows delivers it: WM_HOTKEY with the indicator's id, posted to the banner window.</summary>
    internal static bool PostTakeover(long bannerHandle) =>
        PostMessageW(new IntPtr(bannerHandle), WM_HOTKEY, new IntPtr(ControlIndicator.HotkeyId),
            new IntPtr((TestChord.Key << 16) | (int)TestChord.Modifiers));

    /// <summary>Polls <paramref name="predicate"/> until it holds or <paramref name="timeoutMs"/> passes.</summary>
    internal static async Task<bool> WithinAsync(int timeoutMs, Func<bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    /// <summary>The synchronous twin, for a test that must stay on one thread (it holds a thread-bound hotkey).</summary>
    private static bool Within(int timeoutMs, Func<bool> predicate)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (predicate()) return true;
            Thread.Sleep(25);
        }
        return predicate();
    }

    private static bool BothWindowsVisible(ControlIndicator ci) =>
        ci.RingHandle != 0 && ci.BannerHandle != 0
        && IsWindowVisible(new IntPtr(ci.RingHandle)) && IsWindowVisible(new IntPtr(ci.BannerHandle));

    private static bool NeitherWindowVisible(ControlIndicator ci) =>
        (ci.RingHandle == 0 || !IsWindowVisible(new IntPtr(ci.RingHandle)))
        && (ci.BannerHandle == 0 || !IsWindowVisible(new IntPtr(ci.BannerHandle)));

    private static async Task ShowAsync(ControlIndicator ci)
    {
        ci.BeginAction();
        (await WithinAsync(2000, () => ci.IsVisible)).Should().BeTrue("BeginAction shows the indicator");
    }

    // ---- showing and fading ------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_up_and_nothing_is_registered_before_the_first_action()
    {
        using var ci = new ControlIndicator(Options());

        ci.IsVisible.Should().BeFalse();
        ci.IsHotkeyRegistered.Should().BeFalse("the combination is registered only while the indicator is visible");
        ChordIsFree().Should().BeTrue("other applications keep the combination the rest of the time");
        ci.ConsumeTakeover().Should().BeNull();
    }

    [Fact]
    public async Task BeginAction_puts_up_both_windows()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 30_000));

        await ShowAsync(ci);

        ci.RingHandle.Should().NotBe(0, "the ring window exists");
        ci.BannerHandle.Should().NotBe(0, "the banner window exists");
        ci.RingHandle.Should().NotBe(ci.BannerHandle, "two windows: the ring follows the pointer, the banner does not");
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should()
            .BeTrue("IsVisible is the windows' state, not just a flag");
        ci.EndAction();
    }

    [Fact]
    public async Task Neither_window_is_in_the_window_inventory()
    {
        // Tool windows with no title: A-1's WindowFilter drops them even with include_hidden, so
        // window(list) and snapshot never report or walk the indicator.
        using var ci = new ControlIndicator(Options(lingerMs: 30_000));
        await ShowAsync(ci);
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue();

        var listed = (await new WindowService().ListAsync(includeMinimized: true, includeHidden: true)).Select(w => w.Hwnd).ToArray();

        listed.Should().NotContain(ci.RingHandle, "the ring is not a window the user has open");
        listed.Should().NotContain(ci.BannerHandle, "nor is the banner");
        ci.EndAction();
    }

    [Fact]
    public async Task It_fades_after_the_linger_once_the_last_action_ends()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 1500));
        await ShowAsync(ci);

        ci.EndAction();

        ci.IsVisible.Should().BeTrue("it fades Linger after the last call ends, not at once");
        (await WithinAsync(1500 + 3000, () => !ci.IsVisible)).Should()
            .BeTrue("the pointer-follow tick runs the fade - nothing else calls hide");
        (await WithinAsync(1000, () => NeitherWindowVisible(ci))).Should().BeTrue("and the windows really come down");
    }

    [Fact]
    public async Task Overlapping_actions_keep_it_up_until_the_last_one_ends()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 300));
        ci.BeginAction();
        await ShowAsync(ci);

        ci.EndAction();
        await Task.Delay(1000);

        ci.IsVisible.Should().BeTrue("one call is still in flight; the first to finish must not take it down");
        ci.EndAction();
        (await WithinAsync(3000, () => !ci.IsVisible)).Should().BeTrue("the last End starts the fade");
    }

    [Fact]
    public async Task A_new_action_during_the_fade_keeps_it_up()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 800));
        await ShowAsync(ci);
        ci.EndAction();

        ci.BeginAction();
        await Task.Delay(1800);

        ci.IsVisible.Should().BeTrue("the Begin cancelled the fade; the call is still running");
        ci.EndAction();
        (await WithinAsync(3000, () => !ci.IsVisible)).Should().BeTrue();
    }

    [Fact]
    public async Task Many_threads_beginning_and_ending_leave_it_to_fade_and_free_the_combination()
    {
        // Overlapping HTTP calls arrive on pool threads. A lost refcount update latches the
        // indicator (and the user's combination) for the life of the process.
        using var ci = new ControlIndicator(Options(lingerMs: 300));

        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            ci.BeginAction();
            ci.EndAction();
        });

        (await WithinAsync(5000, () => !ci.IsVisible && ChordIsFree())).Should()
            .BeTrue("every Begin had its End, so it fades and gives the combination back");
    }

    [Fact]
    public async Task BeginAction_returns_promptly()
    {
        // "Nothing is ever delayed": a tool call pays at most a bounded wait (~50 ms) for the overlay
        // thread, however long the show takes. F5: the seam makes the show itself take 2 s, so a
        // BeginAction that waited for the show takes 2 s and one that waits ~50 ms does not - the
        // 200 ms bound tells them apart on any box, which a bound on an ordinary (fast) show never
        // could. The first call is measured: it also starts the overlay thread, as in production.
        using var entered = new ManualResetEventSlim();
        using var ci = new ControlIndicator(Options(lingerMs: 30_000))
        {
            OnShowForTest = () =>
            {
                entered.Set();
                Thread.Sleep(2000);
            },
        };

        var sw = Stopwatch.StartNew();
        ci.BeginAction();
        sw.Stop();

        entered.Wait(TimeSpan.FromSeconds(2)).Should()
            .BeTrue("precondition: the show was really under way (the seam runs inside Show) while BeginAction was timed");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(200), "showing the indicator must not hold up the tool call, however slow the show");
        (await WithinAsync(5000, () => ci.IsVisible)).Should().BeTrue("the slow show still completes, after the call has moved on");
        ci.EndAction();
    }

    // ---- the hotkey --------------------------------------------------------------------------------

    [Fact]
    public async Task While_visible_the_combination_is_registered_and_after_the_fade_it_is_free()
    {
        ChordIsFree().Should().BeTrue("precondition: nothing on this machine holds Ctrl+Alt+Shift+F13");
        using var ci = new ControlIndicator(Options(lingerMs: 300));

        await ShowAsync(ci);

        (await WithinAsync(2000, () => !ChordIsFree())).Should()
            .BeTrue("while the indicator is up the combination is the indicator's");
        ci.IsHotkeyRegistered.Should().BeTrue();

        ci.EndAction();

        (await WithinAsync(3000, ChordIsFree)).Should()
            .BeTrue("after the fade other applications get the combination back");
        ci.IsHotkeyRegistered.Should().BeFalse();
    }

    [Fact]
    public async Task A_posted_WM_HOTKEY_hides_it_at_once_and_records_one_takeover()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 30_000));
        await ShowAsync(ci);
        var before = DateTimeOffset.UtcNow;

        PostTakeover(ci.BannerHandle).Should().BeTrue("the banner window owns the hotkey and receives WM_HOTKEY");

        (await WithinAsync(2000, () => !ci.IsVisible)).Should()
            .BeTrue("takeover hides it at once - even with the call still in flight (no EndAction yet)");
        var after = DateTimeOffset.UtcNow;
        (await WithinAsync(1000, () => NeitherWindowVisible(ci))).Should().BeTrue("both windows come down");

        var record = ci.ConsumeTakeover();
        record.Should().NotBeNull("the next tool result has to be told");
        record!.Hotkey.Should().Be("Ctrl+Alt+Shift+F13", "the record names the configured combination's display text");
        record.At.Should().BeOnOrAfter(before.AddSeconds(-1)).And.BeOnOrBefore(after.AddSeconds(1));
        ci.ConsumeTakeover().Should().BeNull("the note is delivered once");

        (await WithinAsync(2000, ChordIsFree)).Should()
            .BeTrue("hidden means unregistered: the combination goes back to the other applications");
        ci.EndAction();
    }

    [Fact]
    public async Task After_a_takeover_the_next_action_shows_it_again()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 30_000));
        await ShowAsync(ci);
        PostTakeover(ci.BannerHandle);
        (await WithinAsync(2000, () => !ci.IsVisible)).Should().BeTrue();
        ci.EndAction();
        ci.ConsumeTakeover();

        await ShowAsync(ci);

        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue("the next driving call shows it again");
        (await WithinAsync(2000, () => !ChordIsFree())).Should().BeTrue("and takes the combination again");
        ci.EndAction();
    }

    [Fact]
    public async Task An_action_ending_after_a_takeover_does_not_bring_it_back()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 300));
        await ShowAsync(ci);
        PostTakeover(ci.BannerHandle);
        (await WithinAsync(2000, () => !ci.IsVisible)).Should().BeTrue();

        ci.EndAction();
        await Task.Delay(500);

        ci.IsVisible.Should().BeFalse("the call that was in flight ending is not a new driving call");
        NeitherWindowVisible(ci).Should().BeTrue();
    }

    [Fact]
    public async Task WM_HOTKEY_after_the_fade_records_nothing()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 300));
        await ShowAsync(ci);
        var banner = ci.BannerHandle;
        ci.EndAction();
        (await WithinAsync(3000, () => !ci.IsVisible)).Should().BeTrue();

        PostTakeover(banner);   // a stale press that arrives after the fade (the post may fail if the window is gone)
        await Task.Delay(300);

        ci.ConsumeTakeover().Should().BeNull("there was nothing on screen to take over from");
        ci.IsVisible.Should().BeFalse("and it does not come back");
    }

    [Fact]
    public void A_combination_another_application_owns_is_not_a_failure()
    {
        // Synchronous on purpose: the "other application" here is this thread holding the chord,
        // and a thread-bound hotkey can only be released by the thread that registered it.
        RegisterHotKey(IntPtr.Zero, HoldId, TestChord.Modifiers, TestChord.Key).Should()
            .BeTrue("precondition: nothing on this machine holds Ctrl+Alt+Shift+F13");
        try
        {
            // The warning is once per INSTANCE (production has one instance per process, so the
            // operator still sees it once): exactly one across two shows, whatever ran earlier in
            // this test process - and a second instance warns again.
            var log = new RecordingLogger<ControlIndicator>();
            using (var ci = new ControlIndicator(Options(lingerMs: 100), log))
            {
                ci.BeginAction();
                Within(2000, () => ci.IsVisible).Should().BeTrue("the indicator still shows; only the takeover hint is lost");
                ci.IsHotkeyRegistered.Should().BeFalse("someone else owns the combination");
                ci.EndAction();
                Within(3000, () => !ci.IsVisible).Should().BeTrue();

                ci.BeginAction();
                Within(2000, () => ci.IsVisible).Should().BeTrue();
                ci.EndAction();
                Within(3000, () => !ci.IsVisible).Should().BeTrue();
            }

            var warnings = log.MessagesAt(LogLevel.Warning);
            warnings.Should().ContainSingle("two shows with the combination taken are one warning, not two - and not zero");
            warnings[0].Should().Contain("Ctrl+Alt+Shift+F13", "the operator has to know which combination could not be registered");
            warnings[0].Should().ContainEquivalentOf("reserved",
                "Windows reserves some combinations itself (Ctrl+Alt+Del, Win+L): the warning must not blame only another application");

            var second = new RecordingLogger<ControlIndicator>();
            using (var again = new ControlIndicator(Options(lingerMs: 100), second))
            {
                again.BeginAction();
                Within(2000, () => again.IsVisible).Should().BeTrue();
                again.IsHotkeyRegistered.Should().BeFalse();
                again.EndAction();
                Within(3000, () => !again.IsVisible).Should().BeTrue();
            }

            second.MessagesAt(LogLevel.Warning).Should().ContainSingle("the flag is per instance: another indicator's warning does not silence this one");
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HoldId);
        }
    }

    // ---- off, dispose, capture suspension -----------------------------------------------------------

    [Fact]
    public async Task When_off_nothing_is_created_and_nothing_is_registered()
    {
        using var ci = new ControlIndicator(Options(enabled: false));

        ci.BeginAction();
        await Task.Delay(200);   // nothing is allowed to appear late either

        ci.IsVisible.Should().BeFalse("--control-indicator off shows nothing");
        ci.RingHandle.Should().Be(0, "no window is created");
        ci.BannerHandle.Should().Be(0);
        ci.IsHotkeyRegistered.Should().BeFalse("and the combination is never registered");
        ChordIsFree().Should().BeTrue();
        ci.EndAction();
        ci.ConsumeTakeover().Should().BeNull();
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_takes_the_windows_and_the_combination_with_it()
    {
        var ci = new ControlIndicator(Options(lingerMs: 30_000));
        await ShowAsync(ci);
        (await WithinAsync(2000, () => !ChordIsFree())).Should().BeTrue();
        var ring = new IntPtr(ci.RingHandle);
        var banner = new IntPtr(ci.BannerHandle);

        var act = () =>
        {
            ci.Dispose();
            ci.Dispose();
        };

        act.Should().NotThrow("the DI container disposes the singleton; a second dispose must be free");
        ci.IsVisible.Should().BeFalse();
        (await WithinAsync(2000, () => !IsWindow(ring) && !IsWindow(banner))).Should()
            .BeTrue("disposing destroys both windows, even with a call still in flight");
        (await WithinAsync(2000, ChordIsFree)).Should().BeTrue("and releases the combination");
    }

    [Fact]
    public async Task Every_call_after_Dispose_is_a_silent_no_op()
    {
        // Shutdown ordering is not something a tool call controls: a call racing the host's disposal
        // must not crash on the way out, and must not resurrect the windows.
        var ci = new ControlIndicator(Options(lingerMs: 30_000));
        await ShowAsync(ci);
        ci.Dispose();

        var act = () =>
        {
            ci.BeginAction();
            ci.EndAction();
            ci.ConsumeTakeover();
            ci.SuspendForCapture().Dispose();
        };

        act.Should().NotThrow();
        await Task.Delay(200);
        ci.IsVisible.Should().BeFalse("a disposed indicator shows nothing");
        ci.ConsumeTakeover().Should().BeNull();
        ChordIsFree().Should().BeTrue();
    }

    [Fact]
    public void Every_call_on_an_indicator_disposed_before_use_is_a_silent_no_op()
    {
        var ci = new ControlIndicator(Options());
        ci.Dispose();

        var act = () =>
        {
            ci.BeginAction();
            ci.EndAction();
            ci.SuspendForCapture().Dispose();
            ci.Dispose();
        };

        act.Should().NotThrow();
        ci.IsVisible.Should().BeFalse();
        ci.RingHandle.Should().Be(0, "nothing is created after Dispose");
    }

    [Fact]
    public async Task SuspendForCapture_returns_a_disposable_in_every_state()
    {
        // ScreenshotService calls it on every capture, whatever the indicator is doing; a null or a
        // throw here would fail screenshots, which the indicator must never do.
        static void Probe(ControlIndicator ci, string state)
        {
            var scope = ci.SuspendForCapture();
            scope.Should().NotBeNull($"{state}: a scope is always returned");
            var act = () => { scope.Dispose(); scope.Dispose(); };
            act.Should().NotThrow($"{state}: closing the scope (twice) is free");
        }

        using (var fresh = new ControlIndicator(Options(lingerMs: 300)))
        {
            Probe(fresh, "never shown");
            await ShowAsync(fresh);
            Probe(fresh, "visible");
            PostTakeover(fresh.BannerHandle);
            (await WithinAsync(2000, () => !fresh.IsVisible)).Should().BeTrue();
            Probe(fresh, "after a takeover");
            fresh.EndAction();
            await ShowAsync(fresh);
            fresh.EndAction();
            (await WithinAsync(3000, () => !fresh.IsVisible)).Should().BeTrue();
            Probe(fresh, "after the fade");
        }

        using (var off = new ControlIndicator(Options(enabled: false)))
            Probe(off, "off");

        var disposed = new ControlIndicator(Options());
        disposed.Dispose();
        Probe(disposed, "disposed");
    }

    [Windows10_2004Fact]   // reported as SKIPPED below build 19041, never as a silent pass
    public async Task With_display_affinity_the_capture_suspension_leaves_the_windows_up()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 30_000));
        await ShowAsync(ci);
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue();

        ci.IsCaptureExcluded.Should().BeTrue("both windows carry WDA_EXCLUDEFROMCAPTURE on this OS");
        using (ci.SuspendForCapture())
        {
            BothWindowsVisible(ci).Should().BeTrue("affinity keeps them out of the picture, so there is nothing to hide - no flicker");
        }
        ci.EndAction();
    }

    [Fact]
    public async Task Without_display_affinity_the_capture_suspension_hides_and_restores_the_windows()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 30_000)) { ExcludeFromCapture = false };
        await ShowAsync(ci);
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue();

        ci.IsCaptureExcluded.Should().BeFalse("the seam skipped SetWindowDisplayAffinity");
        var scope = ci.SuspendForCapture();
        try
        {
            NeitherWindowVisible(ci).Should()
                .BeTrue("the grab happens right after SuspendForCapture returns, so the windows are down BEFORE it returns");
        }
        finally
        {
            scope.Dispose();
        }

        (await WithinAsync(1000, () => BothWindowsVisible(ci))).Should().BeTrue("closing the scope puts them back");
        ci.IsVisible.Should().BeTrue("a capture is not a takeover and not a fade");
        ci.EndAction();
    }

    // ---- F2: display affinity is trusted only from Windows 10 2004 (build 19041) ---------------------

    /// <summary>The display affinity Windows reports for one of the indicator's windows.</summary>
    private static uint AffinityOf(long hwnd)
    {
        GetWindowDisplayAffinity(new IntPtr(hwnd), out var affinity).Should().BeTrue("the window exists while the indicator is up");
        return affinity;
    }

    [Fact]
    public async Task Before_Windows_10_2004_no_affinity_is_applied_and_every_capture_hides_the_windows()
    {
        // Before build 19041 WDA_EXCLUDEFROMCAPTURE behaves as WDA_MONITOR: SetWindowDisplayAffinity
        // still "succeeds", and every capture then shows two black boxes where the windows are. So
        // below 19041 it is never applied, the indicator does not claim to be excluded, and
        // SuspendForCapture hides the windows around each grab instead.
        using var ci = new ControlIndicator(Options(lingerMs: 30_000)) { OsBuild = 18363 };   // Windows 10 1909
        await ShowAsync(ci);
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue();

        ci.IsCaptureExcluded.Should().BeFalse("below 19041 the affinity would black-box the windows, not hide them");
        AffinityOf(ci.RingHandle).Should().Be(WDA_NONE, "applied below 19041, WDA_EXCLUDEFROMCAPTURE black-boxes the ring in every capture");
        AffinityOf(ci.BannerHandle).Should().Be(WDA_NONE, "and the banner");

        var scope = ci.SuspendForCapture();
        try
        {
            NeitherWindowVisible(ci).Should().BeTrue("without affinity the windows are down BEFORE SuspendForCapture returns - the grab follows at once");
        }
        finally
        {
            scope.Dispose();
        }

        (await WithinAsync(1000, () => BothWindowsVisible(ci))).Should().BeTrue("and back when the capture is over");
        ci.EndAction();
    }

    [Windows10_2004Fact]
    public async Task From_Windows_10_2004_both_windows_are_excluded_from_capture()
    {
        using var ci = new ControlIndicator(Options(lingerMs: 30_000)) { OsBuild = 19041 };
        await ShowAsync(ci);
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue();

        ci.IsCaptureExcluded.Should().BeTrue("from 19041 WDA_EXCLUDEFROMCAPTURE keeps the windows out of every capture");
        AffinityOf(ci.RingHandle).Should().Be(WDA_EXCLUDEFROMCAPTURE);
        AffinityOf(ci.BannerHandle).Should().Be(WDA_EXCLUDEFROMCAPTURE);
        ci.EndAction();
    }

    // ---- F5: a show that faults or stalls never strands the combination or holds up a call -------

    [Fact]
    public async Task A_show_that_faults_once_is_retried_cleanly_by_the_next_tick()
    {
        // The seam throws once, after RegisterHotKey and before painting. A show that is not
        // exception-safe leaves the combination registered with the flag unset: the retry then
        // finds it "taken" by itself, logs a warning that blames a phantom, paints the banner
        // without the hint, and the fade never unregisters it.
        ChordIsFree().Should().BeTrue("precondition: nothing on this machine holds Ctrl+Alt+Shift+F13");
        var shows = 0;
        var log = new RecordingLogger<ControlIndicator>();
        using var ci = new ControlIndicator(Options(lingerMs: 300), log)
        {
            OnShowForTest = () =>
            {
                if (Interlocked.Increment(ref shows) == 1) throw new InvalidOperationException("injected: the first show faults");
            },
        };

        ci.BeginAction();

        (await WithinAsync(2000, () => ci.IsVisible)).Should().BeTrue("the next tick retries the show");
        Volatile.Read(ref shows).Should().BeGreaterThanOrEqualTo(2,
            "precondition: the seam ran inside Show - the first show faulted and a later one got through");
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue();
        ci.IsHotkeyRegistered.Should().BeTrue("the retried show owns the combination: the faulted one gave back what it took");
        log.MessagesAt(LogLevel.Warning).Should().BeEmpty("nothing else holds the combination, so nothing is 'taken'");

        ci.EndAction();
        (await WithinAsync(3000, () => !ci.IsVisible)).Should().BeTrue();
        (await WithinAsync(2000, ChordIsFree)).Should().BeTrue("after the fade nothing is left registered");
    }

    [Fact]
    public async Task A_show_that_faults_until_the_fade_leaves_nothing_registered()
    {
        // Every show faults after RegisterHotKey, so the indicator never reaches "shown" - and a
        // fade that only hides what is shown would never unregister the combination the failed
        // shows took. Once the state no longer wants to show, the registration must go too.
        ChordIsFree().Should().BeTrue("precondition: nothing on this machine holds Ctrl+Alt+Shift+F13");
        var attempts = 0;
        using var ci = new ControlIndicator(Options(lingerMs: 300))
        {
            OnShowForTest = () =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("injected: every show faults");
            },
        };

        ci.BeginAction();
        (await WithinAsync(2000, () => !ChordIsFree())).Should()
            .BeTrue("precondition: the faulting show got as far as registering the combination");
        Volatile.Read(ref attempts).Should().BeGreaterThan(0, "precondition: the fault was injected inside Show");
        ci.IsVisible.Should().BeFalse("no show ever completed");

        ci.EndAction();

        (await WithinAsync(3000, ChordIsFree)).Should()
            .BeTrue("after the fade the combination a failed show registered is given back to the other applications");
        ci.IsHotkeyRegistered.Should().BeFalse();
        ci.IsVisible.Should().BeFalse();
    }

    // ---- GREEN close-out: overlapping captures, the taken combination, shutdown races -------------

    [Fact]
    public async Task Two_overlapping_captures_keep_the_windows_down_until_the_last_one_closes()
    {
        // HTTP clients overlap, so two screenshots can be grabbing at once. The first to finish must
        // not put the windows back while the second is still reading the screen, and closing one
        // scope twice is still ONE close - a double dispose must not release the other capture.
        using var ci = new ControlIndicator(Options(lingerMs: 30_000)) { ExcludeFromCapture = false };
        await ShowAsync(ci);
        (await WithinAsync(2000, () => BothWindowsVisible(ci))).Should().BeTrue();

        var first = ci.SuspendForCapture();
        var second = ci.SuspendForCapture();
        NeitherWindowVisible(ci).Should().BeTrue("both captures hold the windows down");

        first.Dispose();
        first.Dispose();
        await Task.Delay(300);   // a scope closes on the overlay thread: give it many ticks to (wrongly) restore

        NeitherWindowVisible(ci).Should().BeTrue("the second capture is still grabbing; the first closing, even twice, must not bring them back");
        second.Dispose();
        (await WithinAsync(1000, () => BothWindowsVisible(ci))).Should().BeTrue("the last close puts them back");
        ci.EndAction();
    }

    [Fact]
    public async Task A_takeover_during_a_capture_leaves_the_windows_down_when_the_capture_ends()
    {
        // Closing a capture restores what the indicator WANTS, not what it had: after a takeover
        // that is "hidden", so the grab ending must not resurrect the windows the user dismissed.
        using var ci = new ControlIndicator(Options(lingerMs: 30_000)) { ExcludeFromCapture = false };
        await ShowAsync(ci);
        var scope = ci.SuspendForCapture();

        PostTakeover(ci.BannerHandle).Should().BeTrue();
        (await WithinAsync(2000, () => !ci.IsVisible)).Should().BeTrue("the takeover lands while the capture holds the windows down");
        scope.Dispose();
        await Task.Delay(300);

        NeitherWindowVisible(ci).Should().BeTrue("the user took over; the end of a screenshot is not a new driving call");
        ci.IsVisible.Should().BeFalse();
        ci.ConsumeTakeover().Should().NotBeNull("and the takeover still reaches the next result");
        ci.EndAction();
    }

    [Fact]
    public async Task A_capture_while_hidden_changes_nothing_and_leaves_no_suspension_behind()
    {
        // ScreenshotService suspends on every capture, mostly while the indicator is down. That must
        // neither show anything when it closes nor leave a count behind that keeps the NEXT show down.
        using var ci = new ControlIndicator(Options(lingerMs: 300)) { ExcludeFromCapture = false };
        await ShowAsync(ci);
        ci.EndAction();
        (await WithinAsync(3000, () => !ci.IsVisible)).Should().BeTrue();
        (await WithinAsync(1000, () => NeitherWindowVisible(ci))).Should().BeTrue("IsVisible flips just before the windows come down");

        using (ci.SuspendForCapture())
            NeitherWindowVisible(ci).Should().BeTrue("hidden, and a capture does not change that");
        await Task.Delay(300);
        NeitherWindowVisible(ci).Should().BeTrue("closing a capture taken while hidden shows nothing");
        ci.IsVisible.Should().BeFalse();

        await ShowAsync(ci);
        (await WithinAsync(1000, () => BothWindowsVisible(ci))).Should()
            .BeTrue("the next driving call shows the windows: the capture taken while hidden held nothing down");
        ci.EndAction();
    }

    [Fact]
    public async Task Without_display_affinity_a_show_that_starts_during_a_capture_stays_down_until_the_capture_ends()
    {
        // "Never in a picture": without affinity the windows are hidden around each grab. HTTP calls
        // overlap, so a driving call can START while a screenshot is grabbing. The windows that call
        // shows must not come up until the grab is over, or they are in that picture. (A capture that
        // begins while the indicator is hidden is the common case: most screenshots are.)
        using var ci = new ControlIndicator(Options(lingerMs: 30_000)) { ExcludeFromCapture = false };
        await ShowAsync(ci);
        PostTakeover(ci.BannerHandle).Should().BeTrue();   // down again, windows created, affinity known to be off
        (await WithinAsync(2000, () => !ci.IsVisible && NeitherWindowVisible(ci))).Should().BeTrue();
        ci.ConsumeTakeover();
        ci.EndAction();

        var scope = ci.SuspendForCapture();
        try
        {
            ci.BeginAction();   // a driving call arrives mid-grab
            await Task.Delay(300);

            NeitherWindowVisible(ci).Should().BeTrue(
                "a capture is still grabbing: windows that came up now would be in its picture (only the affinity "
                + "fallback has this path; with WDA_EXCLUDEFROMCAPTURE they are invisible to the grab anyway)");
        }
        finally
        {
            scope.Dispose();
        }

        (await WithinAsync(1000, () => BothWindowsVisible(ci))).Should().BeTrue("the grab is over: the driving call's indicator comes up");
        ci.EndAction();
    }

    [Fact]
    public async Task A_capture_scope_closed_after_Dispose_is_a_silent_no_op()
    {
        // A screenshot in flight while the host shuts down closes its scope after the indicator is
        // gone. ScreenshotService closes it in a using, so a throw here would fail that screenshot.
        var ci = new ControlIndicator(Options(lingerMs: 30_000)) { ExcludeFromCapture = false };
        await ShowAsync(ci);
        var ring = new IntPtr(ci.RingHandle);
        var scope = ci.SuspendForCapture();

        ci.Dispose();
        var act = () => scope.Dispose();

        act.Should().NotThrow("the indicator is a courtesy: closing a capture after shutdown is a no-op, not an ObjectDisposedException");
        (await WithinAsync(2000, () => !IsWindow(ring))).Should().BeTrue("the windows went with the indicator");
        ci.IsVisible.Should().BeFalse();
    }

    /// <summary>The banner window's width, and the scale it is drawn at (its DPI / 96).</summary>
    private static (int Width, double Scale) BannerGeometry(ControlIndicator ci)
    {
        var banner = new IntPtr(ci.BannerHandle);
        GetWindowRect(banner, out var r).Should().BeTrue("the banner exists while the indicator is up");
        return (r.Right - r.Left, GetDpiForWindow(banner) / 96.0);
    }

    [Fact]
    public void When_the_combination_is_taken_the_banner_is_painted_without_the_takeover_hint()
    {
        // The design note: if another application owns the combination, "the banner drops the
        // hotkey text". IsHotkeyRegistered alone would stay green if the banner still offered a
        // combination that does nothing, so this measures what was PAINTED: the window is exactly as
        // wide as ControlIndicatorArt draws each text at the window's own DPI, and the hint-less one
        // is narrower. Synchronous: the "other application" is this thread holding the chord.
        int takenWidth;
        double takenScale;
        RegisterHotKey(IntPtr.Zero, HoldId, TestChord.Modifiers, TestChord.Key).Should()
            .BeTrue("precondition: nothing on this machine holds Ctrl+Alt+Shift+F13");
        try
        {
            using var taken = new ControlIndicator(Options(lingerMs: 30_000));
            taken.BeginAction();
            Within(2000, () => BothWindowsVisible(taken)).Should().BeTrue("the indicator still shows; only the hint is lost");
            taken.IsHotkeyRegistered.Should().BeFalse("precondition: someone else owns the combination");
            (takenWidth, takenScale) = BannerGeometry(taken);
            taken.EndAction();
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, HoldId);
        }

        using var free = new ControlIndicator(Options(lingerMs: 30_000));
        free.BeginAction();
        Within(2000, () => BothWindowsVisible(free)).Should().BeTrue();
        free.IsHotkeyRegistered.Should().BeTrue("precondition: released, the combination is the indicator's");
        var (freeWidth, freeScale) = BannerGeometry(free);
        free.EndAction();

        using (var hintless = ControlIndicatorArt.Banner(ControlIndicatorArt.BannerText(null), takenScale))
            takenWidth.Should().Be(hintless.Width, "with the combination taken the banner only says the AI is in control");
        using (var hinted = ControlIndicatorArt.Banner(ControlIndicatorArt.BannerText(TestChord.Display), freeScale))
            freeWidth.Should().Be(hinted.Width, "with the combination registered the banner names it");
        takenWidth.Should().BeLessThan(freeWidth);
    }

    [Fact]
    public async Task Dispose_racing_actions_on_other_threads_never_throws_and_leaves_nothing_behind()
    {
        // The host disposes the singleton while HTTP calls may still be starting on pool threads, some
        // never reaching their End. Nothing may throw into those calls, and nothing may survive the
        // teardown: a show that slipped in after it would leave IsVisible latched and top-most windows
        // on the user's screen for the life of the process.
        for (var round = 0; round < 10; round++)
        {
            var ci = new ControlIndicator(Options(lingerMs: 30_000));
            var seen = new ConcurrentBag<long>();
            using var start = new Barrier(5);
            var callers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                start.SignalAndWait();
                for (var i = 0; i < 20; i++)
                {
                    ci.BeginAction();
                    seen.Add(ci.RingHandle);
                    seen.Add(ci.BannerHandle);
                    using (ci.SuspendForCapture()) { }
                    ci.ConsumeTakeover();
                    if (i % 2 == 0) ci.EndAction();   // the odd calls are still in flight at shutdown
                }
            })).ToArray();

            start.SignalAndWait();
            await Task.Delay(round * 3);   // vary where in the burst the disposal lands
            ci.Dispose();

            var calls = async () => await Task.WhenAll(callers);
            await calls.Should().NotThrowAsync($"round {round}: a call racing the disposal is a silent no-op");
            (await WithinAsync(3000, () => !ci.IsVisible && ci.RingHandle == 0 && ci.BannerHandle == 0)).Should()
                .BeTrue($"round {round}: nothing is up after Dispose, even with calls in flight (visible={ci.IsVisible}, ring={ci.RingHandle})");
            seen.Where(h => h != 0).Distinct().Where(h => IsWindow(new IntPtr(h))).Should()
                .BeEmpty($"round {round}: every window any call saw was destroyed");
        }

        (await WithinAsync(2000, ChordIsFree)).Should().BeTrue("and the combination is released");
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> reported as SKIPPED - not as a silent pass - below Windows 10 2004
/// (build 19041), where <c>WDA_EXCLUDEFROMCAPTURE</c> behaves as <c>WDA_MONITOR</c>. Static on
/// purpose: xunit 2 has no dynamic skip, and an early <c>return</c> reads as green.
/// </summary>
internal sealed class Windows10_2004FactAttribute : FactAttribute
{
    public Windows10_2004FactAttribute()
    {
        if (Environment.OSVersion.Version.Build < 19041)
            Skip = "Needs Windows 10 2004 (build 19041) or later: before it WDA_EXCLUDEFROMCAPTURE acts as WDA_MONITOR.";
    }
}

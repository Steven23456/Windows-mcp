// NOTE (Windows 11): This fixture launches notepad.exe, which may open the modern XAML-based
// Notepad. The modern Notepad exposes a different UI Automation tree than classic Notepad and
// may run under a different process, so element-type-specific assertions can vary.
// GetStateAsync roots at the foreground top-level window (not the focused leaf control), so the
// general "tree is non-empty" assertions hold for whatever window is foreground; this fixture
// additionally foregrounds Notepad so those tests observe it specifically.
//
// Five facts - three about the modern Notepad, two about running these tests - shape everything
// below (measured on 10.0.28000, B-10 phase 1 follow-up):
//
//  1. ONE PROCESS HOSTS EVERY WINDOW. The notepad.exe this fixture launches hands its request to
//     the instance that is already running and exits, so its MainWindowHandle is 0 and the old
//     `App.Close()` closed nothing at all. Worse, `notepad.exe FILE` launched while a Notepad
//     window already exists opens the file as a NEW TAB IN THE EXISTING WINDOW - no new top-level
//     window ever appears. So the fixture identifies its window by diffing the A-1 inventory
//     across the launch AND by title (SelectOpenedWindow): a window that is new is ours outright;
//     a pre-existing window whose title has become "name.txt - Notepad" is holding our TAB, which
//     is a window we must not close (ReusedExistingWindow).
//
//  2. EVERY OPEN TAB IS PERSISTED AS SESSION STATE under
//     %LOCALAPPDATA%\Packages\Microsoft.WindowsNotepad_8wekyb3d8bbwe\LocalState\TabState\*.bin and
//     RESTORED the next time Notepad starts. Killing the process does not remove them: twelve
//     dirty windows from one day of test runs came back the moment a later fixture launched
//     Notepad. So the fixture records the file names in that folder before it launches and, on
//     dispose, deletes whatever appeared while it ran (SweepTabState).
//
//  3. A DIRTY TAB (title starts with '*') does not close on WM_CLOSE or Ctrl+W: Notepad shows an
//     in-window "Save changes to ...?" flyout with Save / Don't save / Cancel buttons and the
//     window stays. So dispose invokes "Don't save" through UIA when it appears
//     (DismissSavePrompt). An empty Untitled tab closes silently.
//
//  4. TWO FIXTURES MUST NEVER LAUNCH CONCURRENTLY. Fact 1 leaves the inventory diff below as the
//     only way to tell which window is ours, and a diff only means anything if nothing else is
//     opening Notepad windows while it runs. xunit parallelises test CLASSES, so four fixtures
//     once launched Notepad inside the same second (four windowless launcher processes with
//     identical start times); each diff then contained the others' windows, every fixture picked
//     a window that was not its own, and the classes went on to minimise, close and type into
//     each other's windows - twelve failures across UIAutomationServiceTests,
//     UIAutomationSnapshotDesktopTests, WindowForegroundDesktopTests, NotepadFixtureSelfTests and
//     ScreenshotWgcCaptureTests in the desktop bracket, with every one of those classes green on
//     its own. So EVERY class that constructs a NotepadFixture - as an IClassFixture<NotepadFixture>
//     or with `new` inside a test - carries [Collection(DesktopCollection.Name)], which serialises
//     them. Add that attribute to any new one; there is no isolation to be had from the process.
//
//  5. THE LAUNCHER PROCESS INHERITS THE TEST HOST'S STDOUT/STDERR. Application.Launch runs
//     notepad.exe with UseShellExecute=false (FlaUI's default), so the child inherits the test
//     host's standard handles. That child is the windowless launcher from fact 1: it hands the
//     request to the running Notepad instance and exits, but until it does it holds the inherited
//     pipe open. When `dotnet test` output is piped, the pipe therefore stays open past the
//     summary line and a desktop bracket looks like it has HUNG at the end. It has not - the pipe
//     drains as the launchers exit. Avoiding it means launching through FlaUI's
//     Application.Launch(ProcessStartInfo) overload with UseShellExecute=true, since ShellExecuteEx
//     does not pass handles to the child; that changes the launch path under every desktop test,
//     so it needs a full desktop bracket to validate and is deliberately NOT done here.
//
// Dispose therefore leaves NOTHING behind: the window (or, in the shared case, only our tab) is
// closed, the save prompt is answered, the owning process is terminated only when this fixture was
// the sole owner and it lingers windowless, and the tab-state folder is put back the way it was.

using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;

namespace WindowsMcp.Tests.Fixtures;

public sealed class NotepadFixture : IDisposable
{
    private const uint WM_CLOSE = 0x0010;
    private const byte VK_MENU = 0x12;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_W = 0x57;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    /// <summary>How many fixtures are alive right now: the last one out may terminate Notepad.</summary>
    private static int _live;

    /// <summary>The file this fixture asked Notepad to open, or null for an empty Untitled tab.</summary>
    private readonly string? _openFile;

    /// <summary>True when no Notepad window existed before this fixture launched one.</summary>
    private readonly bool _soleOwner;

    /// <summary>The tab-state file names present before the launch - the set dispose restores.</summary>
    private readonly IReadOnlySet<string> _tabStateBefore;

    /// <summary>The pid that owns <see cref="Window"/>, or 0 when no window was identified.</summary>
    private readonly int _windowPid;

    public Application App { get; }
    public UIA3Automation Automation { get; }

    /// <summary>
    /// The top-level window this fixture's launch produced, as A-1's inventory sees it - null only
    /// when neither a new window nor a retitled existing one appeared. Tests must target this
    /// handle rather than searching for "Notepad" by title: on a machine with several Notepad
    /// windows a title search picks an arbitrary one.
    /// </summary>
    public WindowInfo? Window { get; }

    /// <summary>The handle of <see cref="Window"/>, or 0 when there is none.</summary>
    public long Hwnd => Window?.Hwnd ?? 0;

    /// <summary>
    /// The title <see cref="Window"/> carried when it was identified ("" when there is no window).
    /// A tab that is later typed into gains a leading '*', so treat this as a fragment to search
    /// for, not a string to compare against a live inventory.
    /// </summary>
    public string Title => Window?.Title ?? string.Empty;

    /// <summary>
    /// True when no Notepad window existed at all before this fixture launched one - the only
    /// state in which terminating notepad.exe can be this fixture's business.
    /// </summary>
    public bool SoleOwner => _soleOwner;

    /// <summary>
    /// True when the launch did NOT produce a window of its own: modern Notepad opened the file as
    /// a tab inside a window that already existed and belongs to someone else. Dispose then closes
    /// only that TAB, and a test whose subject is closing a WINDOW must bail - WM_CLOSE on a shared
    /// window would take somebody else's tabs with it.
    /// </summary>
    public bool ReusedExistingWindow { get; }

    public NotepadFixture() : this(null) { }

    /// <param name="openFile">
    /// A file for Notepad to open, so the window (or tab) carries a title only this fixture's
    /// window has. Null launches an empty "Untitled - Notepad", which is what the shared fixture
    /// users expect. Internal because xUnit allows exactly one public constructor on a class
    /// fixture.
    /// </param>
    internal NotepadFixture(string? openFile)
    {
        Interlocked.Increment(ref _live);
        _openFile = openFile;
        _tabStateBefore = TabStateFiles(TabStateDirectory);
        var before = NotepadWindows().Select(w => w.Hwnd).ToHashSet();
        _soleOwner = before.Count == 0;

        App = openFile is null
            ? Application.Launch("notepad.exe")
            : Application.Launch("notepad.exe", $"\"{openFile}\"");
        Automation = new UIA3Automation();

        Window = WaitForOpenedWindow(before, openFile);
        ReusedExistingWindow = Window is not null && before.Contains(Window.Hwnd);
        _windowPid = Window?.Pid ?? 0;
        Thread.Sleep(400);   // let the window finish drawing before anything reads it

        BringToForeground();
    }

    /// <summary>
    /// Bring this fixture's Notepad window to the foreground so foreground-rooted state queries
    /// observe it. Also used by tests that open another window and must hand the desktop back
    /// afterwards.
    /// Best effort: modern (XAML) Notepad may hand off to a different process and time out
    /// here - that's fine, the service falls back to whatever window is foreground.
    /// </summary>
    public void BringToForeground()
    {
        var hwnd = (IntPtr)Hwnd;
        if (hwnd == IntPtr.Zero)
        {
            // No window of our own was identified; fall back to whatever FlaUI calls the main
            // window of the process we launched (classic Notepad, and pre-B-10 behaviour).
            try
            {
                var window = App.GetMainWindow(Automation, TimeSpan.FromSeconds(5));
                hwnd = window?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero;
            }
            catch { /* foreground is best-effort; not required for correctness */ }
        }
        if (hwnd == IntPtr.Zero) return;

        for (int attempt = 0; attempt < 3 && GetForegroundWindow() != hwnd; attempt++)
        {
            // Arrangement, not the code under test: a synthetic ALT lifts Windows' foreground lock
            // for one more request. ForegroundLadder is what the B-10 tests exercise; this only
            // has to get the desktop into the shape a test starts from.
            if (attempt > 0)
            {
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            SetForegroundWindow(hwnd);
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Close what this fixture opened, answer the save prompt if one appears, terminate a
    /// windowless leftover process when this fixture owned it, and put the tab-state folder back
    /// the way it was. Every step is best effort and logs what it could not do: a fixture that
    /// throws on the way out turns an unrelated test red.
    /// </summary>
    public void Dispose()
    {
        bool last = Interlocked.Decrement(ref _live) == 0;
        try { CloseWhatWeOpened(last); }
        catch (Exception ex) { Log($"closing '{Title}' failed: {ex.Message}"); }
        try { Automation.Dispose(); } catch { /* best effort */ }
        try { SweepTabState(); }
        catch (Exception ex) { Log($"tab-state sweep failed: {ex.Message}"); }
    }

    // ---- pure helpers: no desktop, no Notepad, unit-tested in NotepadFixtureHelperTests --------

    /// <summary>
    /// Modern Notepad's session store: every open tab is a file here, and Notepad restores all of
    /// them on its next start whether or not the process was killed.
    /// </summary>
    internal static string TabStateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", "Microsoft.WindowsNotepad_8wekyb3d8bbwe", "LocalState", "TabState");

    /// <summary>
    /// The names (not paths) of every file in <paramref name="directory"/>, compared
    /// case-insensitively. A directory that does not exist - classic Notepad, or a machine that has
    /// never opened the modern one - is an empty set, not an error.
    /// </summary>
    internal static IReadOnlySet<string> TabStateFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>Names in <paramref name="after"/> that were not in <paramref name="before"/>.</summary>
    internal static string[] NewTabStateFiles(IReadOnlySet<string> before, IReadOnlySet<string> after)
        => after.Where(n => !before.Contains(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();

    /// <summary>
    /// The exact names to ask UIA for when looking for the discard button of the "Save changes to
    /// ...?" flyout. Windows spells the apostrophe U+2019 in some builds and U+0027 in others, so
    /// both are tried before the predicate sweep below. Written as escapes so this file stays
    /// ASCII and nothing can normalise them on the way to the compiler (the A-13 precedent).
    /// </summary>
    internal static IReadOnlyList<string> DiscardButtonNames { get; } =
        ["Don't save", "Don\u2019t save", "Don't Save", "Don\u2019t Save"];

    /// <summary>
    /// True for the discard button of the save prompt and for nothing else on it: "Save" and
    /// "Cancel" must not match, or dispose would write the test's rubbish into the user's file.
    /// Both apostrophes and any casing are accepted.
    /// </summary>
    internal static bool IsDiscardButtonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var flat = name.Replace('\u2019', '\'').Replace('\u02bc', '\'').Trim();
        return flat.Equals("Don't save", StringComparison.OrdinalIgnoreCase)
            || flat.Equals("Dont save", StringComparison.OrdinalIgnoreCase)
            || flat.Equals("Do not save", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for the close button of a tab. Only ever applied INSIDE the subtree of the tab item we
    /// mean to close: the caption bar's own "Close" button is named the same, and invoking that
    /// would take the whole window, other people's tabs included.
    /// </summary>
    internal static bool IsCloseTabButtonName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.Contains("close", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="title"/> is the title Notepad gives a window showing
    /// <paramref name="file"/>: "name.txt - Notepad", "*name.txt - Notepad" when dirty, or either
    /// of those without the extension. Callers pass a file whose name carries a GUID marker, so a
    /// substring test cannot collide with another window.
    /// </summary>
    internal static bool TitleNamesFile(string? title, string? file)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(file)) return false;
        var name = Path.GetFileName(file);
        var stem = Path.GetFileNameWithoutExtension(file);
        return (name.Length > 0 && title.Contains(name, StringComparison.OrdinalIgnoreCase))
            || (stem.Length > 0 && title.Contains(stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Which window the launch produced, given the handles that existed before it
    /// (<paramref name="before"/>), the inventory now, and the file that was opened.
    /// <list type="bullet">
    /// <item>No file: the first window that is NEW. There is no way to tell one
    /// "Untitled - Notepad" from another, so a pre-existing window is never claimed.</item>
    /// <item>A file: a NEW window whose title names the file wins; otherwise a PRE-EXISTING window
    /// whose title now names it - modern Notepad opened our file as a tab inside it.</item>
    /// </list>
    /// Ties break on z-order, so the frontmost candidate wins.
    /// </summary>
    internal static WindowInfo? SelectOpenedWindow(
        IReadOnlySet<long> before, IReadOnlyList<WindowInfo> now, string? openFile)
    {
        if (now.Count == 0) return null;
        if (openFile is null)
            return now.Where(w => !before.Contains(w.Hwnd)).OrderBy(w => w.ZOrder).FirstOrDefault();

        var named = now.Where(w => TitleNamesFile(w.Title, openFile)).OrderBy(w => w.ZOrder).ToArray();
        return named.FirstOrDefault(w => !before.Contains(w.Hwnd)) ?? named.FirstOrDefault();
    }

    /// <summary>
    /// May this fixture terminate the Notepad process? Only when it was the sole owner (nothing of
    /// Notepad's was on screen before it launched), it is the last fixture alive, the window it
    /// opened is gone, and no Notepad window is left that would belong to someone else.
    /// </summary>
    internal static bool MayTerminateNotepad(
        bool soleOwner, bool lastFixtureAlive, bool ourWindowGone, bool anyNotepadWindowRemains)
        => soleOwner && lastFixtureAlive && ourWindowGone && !anyNotepadWindowRemains;

    // ---- dispose, step by step ----------------------------------------------------------------

    private void CloseWhatWeOpened(bool lastFixtureAlive)
    {
        if (Window is null)
        {
            Log("no window of its own was identified, so nothing is closed by handle; the "
                + "tab-state sweep is the only cleanup left.");
            try { App.Close(); } catch { /* best effort */ }
            return;
        }

        var hwnd = (IntPtr)Window.Hwnd;
        if (!IsWindow(hwnd)) { TerminateIfLingering(lastFixtureAlive); return; }

        BringToForeground();

        if (ReusedExistingWindow) { CloseOurTabOnly(hwnd); return; }

        // Our own window: WM_CLOSE it (no synthetic keystrokes - a keystroke lands on whatever has
        // the foreground, and this runs while the desktop is being handed around). A dirty tab
        // answers with the save prompt instead of closing, and a window holding a second tab needs
        // a second round, so the whole thing repeats.
        for (var round = 0; round < 3 && IsWindow(hwnd); round++)
        {
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            DismissSavePrompt(hwnd);
            WaitForWindowGone(hwnd, TimeSpan.FromSeconds(2));
        }

        if (IsWindow(hwnd))
        {
            Log($"'{Title}' (hwnd {Window.Hwnd}) would not close after three rounds of WM_CLOSE; "
                + "it is being left on the desktop rather than killed.");
            return;
        }

        TerminateIfLingering(lastFixtureAlive);
    }

    /// <summary>
    /// Our file was opened as a tab in a window that already existed. Close THAT TAB and nothing
    /// else: the window is somebody else's and WM_CLOSE would take their tabs with it. The tab's
    /// own close button is the safe route; Ctrl+W is the fallback, and only when the window we mean
    /// is verifiably the foreground one at the instant the keystroke is sent.
    /// </summary>
    private void CloseOurTabOnly(IntPtr hwnd)
    {
        if (!TryCloseTabThroughUia(hwnd)) TrySendCtrlW(hwnd);
        DismissSavePrompt(hwnd);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (!OurTabIsStillOpen()) return;
            Thread.Sleep(150);
        }
        Log($"the tab for '{_openFile}' is still open in window {Window?.Hwnd}, which existed "
            + "before this fixture ran - so the window itself is not this fixture's to close.");
    }

    /// <summary>True while some Notepad window's title still names the file we opened.</summary>
    private bool OurTabIsStillOpen()
        => NotepadWindows().Any(w => TitleNamesFile(w.Title, _openFile));

    private bool TryCloseTabThroughUia(IntPtr hwnd)
    {
        try
        {
            var window = Automation.FromHandle(hwnd);
            if (window is null) return false;
            var tab = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
                .FirstOrDefault(t => TitleNamesFile(SafeName(t), _openFile));
            if (tab is null) return false;

            try { tab.Patterns.SelectionItem.PatternOrDefault?.Select(); } catch { /* best effort */ }
            var close = tab.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => IsCloseTabButtonName(SafeName(b)));
            return close is not null && TryInvoke(close);
        }
        catch { return false; }
    }

    /// <summary>
    /// Ctrl+W to the window, but only if it really has the foreground when the keys go out - a
    /// synthetic Ctrl+W that lands on a browser closes the user's tab.
    /// </summary>
    private bool TrySendCtrlW(IntPtr hwnd)
    {
        BringToForeground();
        if (GetForegroundWindow() != hwnd)
        {
            Log($"refusing to send Ctrl+W: window {Window?.Hwnd} is not the foreground window, so "
                + "the keystroke would land somewhere else.");
            return false;
        }
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_W, 0, 0, UIntPtr.Zero);
        keybd_event(VK_W, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        return true;
    }

    /// <summary>
    /// Answer the "Save changes to ...?" flyout with "Don't save" if it shows up within ~1.5 s.
    /// Returns true when a button was invoked.
    /// </summary>
    private bool DismissSavePrompt(IntPtr hwnd)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsWindow(hwnd)) return false;   // it closed silently: nothing was dirty
            var discard = FindDiscardButton(hwnd);
            if (discard is not null && TryInvoke(discard))
            {
                Thread.Sleep(300);   // let the window act on the answer
                return true;
            }
            Thread.Sleep(150);
        }
        return false;
    }

    private AutomationElement? FindDiscardButton(IntPtr hwnd)
    {
        try
        {
            var window = Automation.FromHandle(hwnd);
            if (window is null) return null;
            foreach (var name in DiscardButtonNames)
            {
                var hit = window.FindFirstDescendant(cf => cf.ByName(name));
                if (hit is not null) return hit;
            }
            // However this build spells it, it is a Button on the flyout and it is not Save.
            return window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(b => IsDiscardButtonName(SafeName(b)));
        }
        catch { return null; }
    }

    private static bool TryInvoke(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Invoke.PatternOrDefault is { } invoke) { invoke.Invoke(); return true; }
            if (element.Patterns.LegacyIAccessible.PatternOrDefault is { } legacy)
            {
                legacy.DoDefaultAction();
                return true;
            }
        }
        catch { /* the element can vanish under us the moment the window acts */ }
        return false;
    }

    private static string? SafeName(AutomationElement element)
    {
        try { return element.Properties.Name.ValueOrDefault; } catch { return null; }
    }

    private static void WaitForWindowGone(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && IsWindow(hwnd)) Thread.Sleep(100);
    }

    /// <summary>
    /// Our window is gone but the process that hosted it can stay behind with no window at all
    /// (observed repeatedly). Give it ~2 s to exit on its own, then terminate it - but only under
    /// <see cref="MayTerminateNotepad"/>, i.e. only when nothing of Notepad's was on screen before
    /// this fixture launched and nothing of Notepad's is on screen now.
    /// </summary>
    private void TerminateIfLingering(bool lastFixtureAlive)
    {
        if (_windowPid == 0) return;
        try
        {
            using var proc = Process.GetProcessById(_windowPid);
            if (!proc.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase)) return;
            if (proc.WaitForExit(2000)) return;

            var remains = NotepadWindows().Length > 0;
            if (!MayTerminateNotepad(_soleOwner, lastFixtureAlive, ourWindowGone: true, remains))
            {
                Log($"notepad.exe (pid {_windowPid}) is still running after its window closed, but "
                    + "another Notepad window or fixture is in play, so it is left alone.");
                return;
            }
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
        }
        catch (ArgumentException) { /* already exited: the good case */ }
        catch (InvalidOperationException) { /* likewise */ }
        catch (Exception ex) { Log($"could not terminate notepad.exe (pid {_windowPid}): {ex.Message}"); }
    }

    /// <summary>
    /// Put the tab-state folder back exactly as the constructor found it. Anything that appeared
    /// while this fixture ran is a tab this fixture opened, and leaving it would make the NEXT
    /// Notepad launch restore this test's window - that is how twelve of them accumulated.
    /// </summary>
    private void SweepTabState()
    {
        var directory = TabStateDirectory;
        var added = NewTabStateFiles(_tabStateBefore, TabStateFiles(directory));
        if (added.Length == 0) return;

        Log($"{added.Length} tab-state file(s) appeared while this fixture ran "
            + $"({string.Join(", ", added)}); deleting them so Notepad does not restore this "
            + "test's tab on its next start.");
        foreach (var name in added)
        {
            var path = Path.Combine(directory, name);
            try { File.Delete(path); }
            catch
            {
                Thread.Sleep(300);   // Notepad may still have been writing it
                try { File.Delete(path); }
                catch (Exception ex) { Log($"could not delete {name}: {ex.Message}"); }
            }
        }

        var leftover = NewTabStateFiles(_tabStateBefore, TabStateFiles(directory));
        if (leftover.Length > 0)
            Log($"tab state is STILL dirty: {string.Join(", ", leftover)}. Notepad will restore "
                + "these on its next start; delete them by hand.");
    }

    private static void Log(string message) => Console.Error.WriteLine($"NotepadFixture: {message}");

    /// <summary>Every top-level window A-1 lists whose process is Notepad, minimised ones included.</summary>
    private static WindowInfo[] NotepadWindows()
    {
        try
        {
            return new WindowService().ListAsync(includeMinimized: true).GetAwaiter().GetResult()
                .Where(w => w.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch { return []; }
    }

    /// <summary>
    /// Poll the inventory until <see cref="SelectOpenedWindow"/> can name the window (or the shared
    /// window holding the tab) the launch produced. Up to ~10 s: a cold Notepad under Defender is
    /// slow. If a file was given and no title ever named it, a window that is merely NEW is
    /// returned as a last resort - the title read can lag the window.
    /// </summary>
    private static WindowInfo? WaitForOpenedWindow(HashSet<long> before, string? openFile)
    {
        WindowInfo? anyNewWindow = null;
        for (int i = 0; i < 40; i++)
        {
            var now = NotepadWindows();
            var picked = SelectOpenedWindow(before, now, openFile);
            if (picked is not null) return picked;
            anyNewWindow ??= SelectOpenedWindow(before, now, null);
            Thread.Sleep(250);
        }
        if (anyNewWindow is not null)
            Log($"no Notepad window or tab was ever titled after '{openFile}'; falling back to the "
                + $"window that appeared during the launch (hwnd {anyNewWindow.Hwnd}).");
        return anyNewWindow;
    }
}

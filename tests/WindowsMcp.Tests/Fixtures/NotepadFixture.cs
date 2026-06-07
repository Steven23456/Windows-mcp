// NOTE (Windows 11): This fixture launches notepad.exe, which may open the modern XAML-based
// Notepad. The modern Notepad exposes a different UI Automation tree than classic Notepad and
// may run under a different process, so element-type-specific assertions can vary.
// GetStateAsync roots at the foreground top-level window (not the focused leaf control), so the
// general "tree is non-empty" assertions hold for whatever window is foreground; this fixture
// additionally foregrounds Notepad so those tests observe it specifically.

using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.UIA3;

namespace WindowsMcp.Tests.Fixtures;

public sealed class NotepadFixture : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public Application App { get; }
    public UIA3Automation Automation { get; }

    public NotepadFixture()
    {
        App = Application.Launch("notepad.exe");
        Automation = new UIA3Automation();
        Thread.Sleep(800);   // Allow notepad startup time

        // Bring Notepad to the foreground so foreground-rooted state queries observe it.
        // Best effort: modern (XAML) Notepad may hand off to a different process and time
        // out here — that's fine, the service falls back to whatever window is foreground.
        try
        {
            var window = App.GetMainWindow(Automation, TimeSpan.FromSeconds(5));
            var hwnd = window?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
            Thread.Sleep(200);
        }
        catch { /* foreground is best-effort; not required for correctness */ }
    }

    public void Dispose()
    {
        Automation.Dispose();
        try { App.Close(); } catch { /* best effort */ }
    }
}

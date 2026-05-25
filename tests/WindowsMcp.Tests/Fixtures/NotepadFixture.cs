// NOTE (Windows 11): This fixture launches notepad.exe which may open the modern XAML-based
// Notepad on Windows 11. The modern Notepad exposes a different UI Automation tree than classic
// Notepad. If UIAutomation tests fail because expected element types are not found, this is a
// known environmental difference — do not attempt to force-launch classic Notepad.
// Document failures in test output and recategorize tests if needed.

using FlaUI.Core;
using FlaUI.UIA3;

namespace WindowsMcp.Tests.Fixtures;

public sealed class NotepadFixture : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; }

    public NotepadFixture()
    {
        App = Application.Launch("notepad.exe");
        Automation = new UIA3Automation();
        Thread.Sleep(800);   // Allow notepad startup time
    }

    public void Dispose()
    {
        Automation.Dispose();
        try { App.Close(); } catch { /* best effort */ }
    }
}

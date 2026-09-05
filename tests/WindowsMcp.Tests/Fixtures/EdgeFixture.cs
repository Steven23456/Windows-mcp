using System.Diagnostics;
using System.Management;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// A-5 phase 1: one Chromium window on a local page, for the tests that need a real DOM.
/// Serves <see cref="LocalHttpServerFixture"/>'s <c>/a5</c> probe page and launches Edge with
/// <c>--app=</c> (no tab strip, no address bar) into a throwaway profile directory, so nothing
/// touches the developer's own Edge profile or session.
/// </summary>
/// <remarks>
/// <para>
/// Every test using this fixture is <c>[Trait("Category", "UIAutomation")]</c>: it needs the
/// interactive desktop, it opens a real window, and Chromium builds its accessibility tree lazily
/// on the first UIA query — none of which survives a headless or background run.
/// </para>
/// <para>
/// When msedge.exe is not installed the fixture comes up with <see cref="Available"/> false
/// instead of throwing, and each test returns early: a machine without Edge is a machine that
/// cannot run this bracket, not a failure. (xunit 2.9 has no dynamic skip.)
/// </para>
/// </remarks>
public sealed class EdgeFixture : IDisposable
{
    /// <summary>The &lt;title&gt; of the probe page — and, in --app mode, the window's title.</summary>
    public const string PageTitle = "A5 Probe Page";

    private static readonly string[] EdgePaths =
    [
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    ];

    private readonly LocalHttpServerFixture? _server;
    private readonly Process? _edge;
    private readonly string _profileDir;

    /// <summary>False when Edge is not installed, or its window never appeared: the tests skip themselves.</summary>
    public bool Available { get; }

    /// <summary>The page the window is showing, e.g. <c>http://127.0.0.1:51234/a5</c>.</summary>
    public string PageUrl { get; } = "";

    /// <summary>The base the URL must start with, e.g. <c>http://127.0.0.1:51234</c>.</summary>
    public string BaseUrl { get; } = "";

    /// <summary>The title the A-1 inventory reports for the browser window, as an agent would find it.</summary>
    public string WindowTitle { get; } = "";

    public long Hwnd { get; }

    public EdgeFixture()
    {
        _profileDir = Path.Combine(Path.GetTempPath(), "wmcp-a5-edge-" + Guid.NewGuid().ToString("N"));

        var exe = EdgePaths.FirstOrDefault(File.Exists);
        if (exe is null) return;

        Directory.CreateDirectory(_profileDir);
        _server = new LocalHttpServerFixture();
        BaseUrl = _server.BaseUrl;
        PageUrl = _server.UrlFor("/a5");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
        };
        foreach (var arg in new[]
                 {
                     $"--app={PageUrl}",
                     "--new-window",
                     $"--user-data-dir={_profileDir}",
                     "--no-first-run",
                     "--no-default-browser-check",
                 })
            psi.ArgumentList.Add(arg);

        try { _edge = Process.Start(psi); }
        catch (Exception) { return; }

        var window = WaitForWindow(TimeSpan.FromSeconds(15));
        if (window is null) return;

        WindowTitle = window.Title;
        Hwnd = window.Hwnd;
        Available = true;
    }

    /// <summary>
    /// The browser window as A-1 sees it — by title, which is how an agent would find it too, and
    /// the only way that works when Chromium's window belongs to a different process than the one
    /// <see cref="Process.Start(ProcessStartInfo)"/> returned.
    /// </summary>
    private static WindowInfo? WaitForWindow(TimeSpan timeout)
    {
        var service = new WindowService();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            WindowInfo[] windows;
            try { windows = service.ListAsync().GetAwaiter().GetResult(); }
            catch { windows = []; }

            var match = windows.FirstOrDefault(w =>
                w.IsBrowser && w.Title.Contains(PageTitle, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;

            Thread.Sleep(250);
        }
        return null;
    }

    public void Dispose()
    {
        // The process this fixture started, plus anything Chromium forked off it.
        if (_edge is not null)
        {
            try { _edge.Kill(entireProcessTree: true); } catch { /* already gone */ }
            try { _edge.WaitForExit(5000); } catch { /* best effort */ }
            _edge.Dispose();
        }

        // Chromium may have handed the window to a browser process that is not a child of the
        // launcher; the throwaway profile directory on its command line is what identifies ours.
        KillByProfileDirectory();

        _server?.Dispose();

        try { if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, recursive: true); }
        catch { /* the profile is under TEMP; a locked file there is not worth failing a run over */ }
    }

    private void KillByProfileDirectory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'");
            foreach (var row in searcher.Get().Cast<ManagementObject>())
            {
                using (row)
                {
                    var commandLine = row["CommandLine"] as string;
                    if (commandLine is null || !commandLine.Contains(_profileDir, StringComparison.OrdinalIgnoreCase))
                        continue;   // somebody else's Edge - never touch it

                    try
                    {
                        using var process = Process.GetProcessById(Convert.ToInt32(row["ProcessId"]));
                        process.Kill(entireProcessTree: true);
                    }
                    catch { /* raced us to exit */ }
                }
            }
        }
        catch { /* WMI unavailable: the process-tree kill above is the primary path */ }
    }
}

/// <summary>
/// One Edge window for every A-5 desktop test, and no two of them open at once.
/// <para>
/// As a per-CLASS fixture each test class got its own Edge, xunit ran the classes in parallel, and
/// two windows titled "A5 Probe Page" were open at the same time: <c>scope=window</c>'s substring
/// match then found both and <c>HttpTransportDomSnapshotTests</c>'s "one browser window was in
/// scope" assertion failed with <c>found 2</c> — only when the classes ran together, never alone.
/// A collection fixture is one browser shared by the whole collection AND serialises the classes
/// in it, which is what makes the desktop bracket runnable as a whole
/// (<c>--filter Category=UIAutomation</c>).
/// </para>
/// </summary>
[CollectionDefinition(EdgeCollection.Name)]
public sealed class EdgeCollection : ICollectionFixture<EdgeFixture>
{
    /// <summary>The name both A-5 desktop test classes carry on <c>[Collection]</c>.</summary>
    public const string Name = "Edge (A-5 probe page)";
}

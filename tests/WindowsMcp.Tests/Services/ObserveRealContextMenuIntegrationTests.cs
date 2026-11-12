using System.Diagnostics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using WindowsMcp.Tests.Fixtures;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-5 round 2 R3 against a REAL open menu: a WinForms window with a ContextMenuStrip shown over
/// it, in a child <c>powershell.exe</c> (the test assembly does not reference WinForms). The
/// review-agent's host, made a fixture: the form is shown without activation and the menu is
/// opened from code (<c>ContextMenuStrip.Show</c>), so no input is injected. The menu is its own
/// untitled top-level window of the host process, outside the window inventory - the case the
/// round-1 observation could not see. Slow (a PowerShell cold start plus an Add-Type compile).
/// </summary>
public sealed class RealContextMenuHost : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const string Script = """
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
        using System.Windows.Forms;
        public class NoActForm : Form { protected override bool ShowWithoutActivation { get { return true; } } }
        "@ -WarningAction SilentlyContinue
        $f = New-Object NoActForm
        $f.Text = $args[1]; $f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point(5, 5); $f.Size = New-Object System.Drawing.Size(300,200)
        $b = New-Object System.Windows.Forms.Button; $b.Text = 'MenuHostBtn'; $b.Size = New-Object System.Drawing.Size(150,30); $f.Controls.Add($b)
        $cms = New-Object System.Windows.Forms.ContextMenuStrip
        $cms.AutoClose = $false
        [void]$cms.Items.Add('CtxItemA'); [void]$cms.Items.Add('CtxItemB'); [void]$cms.Items.Add('CtxItemC')
        $f.Show()
        $cms.Show($f, (New-Object System.Drawing.Point(260, 120)))
        "ok $($f.Handle) $($cms.Handle)" | Out-File -Encoding ascii $args[0]
        $t = New-Object System.Windows.Forms.Timer; $t.Interval = 300000; $t.add_Tick({ [System.Windows.Forms.Application]::Exit() }); $t.Start()
        [System.Windows.Forms.Application]::Run()
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wmcp-e5-cms-" + Guid.NewGuid().ToString("N"));
    private readonly Process? _process;

    public string Title { get; } = "wmcp-e5-cms-" + Guid.NewGuid().ToString("N")[..12];
    public long FormHandle { get; }
    public long MenuHandle { get; }
    public IntPtr ForegroundBefore { get; }
    public IntPtr ForegroundAfter { get; }
    public string? Failure { get; }

    public RealContextMenuHost()
    {
        Directory.CreateDirectory(_dir);
        var script = Path.Combine(_dir, "host.ps1");
        var ready = Path.Combine(_dir, "handles.txt");
        File.WriteAllText(script, Script);
        ForegroundBefore = GetForegroundWindow();
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, ready, Title },
        };
        _process = Process.Start(psi);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        while (DateTime.UtcNow < deadline && !File.Exists(ready) && _process is { HasExited: false })
            Thread.Sleep(250);
        Thread.Sleep(500);   // let the menu finish painting
        ForegroundAfter = GetForegroundWindow();
        var parts = File.Exists(ready) ? File.ReadAllText(ready).Trim().Split(' ') : [];
        if (parts.Length == 3 && parts[0] == "ok")
        {
            FormHandle = long.Parse(parts[1]);
            MenuHandle = long.Parse(parts[2]);
        }
        else
            Failure = _process is null ? "powershell.exe did not start" : $"the host did not report its handles (exited: {_process.HasExited})";
    }

    public void Dispose()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[Trait("Category", "Integration")]
[Collection(DesktopCollection.Name)]
public sealed class ObserveRealContextMenuIntegrationTests : IClassFixture<RealContextMenuHost>, IDisposable
{
    private readonly RealContextMenuHost _host;
    private readonly UIAutomationService _svc;

    public ObserveRealContextMenuIntegrationTests(RealContextMenuHost host)
    {
        _host = host;
        var input = new Mock<IInputService>(MockBehavior.Strict);
        input.Setup(i => i.GetCursorPositionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new CursorPosition(5, 5));
        _svc = new UIAutomationService(input.Object, new WindowService());
    }

    public void Dispose() => _svc.Dispose();

    private void HostIsUp() => _host.Failure.Should().BeNull("precondition: the WinForms host showed its form and its menu");

    [Fact]
    public void The_host_opened_its_menu_without_taking_the_foreground()
    {
        HostIsUp();
        _host.ForegroundAfter.Should().Be(_host.ForegroundBefore, "the form and the menu are shown without activation");
    }

    [Fact]
    public async Task Observing_the_form_by_handle_with_popups_lists_the_open_menus_items()
    {
        HostIsUp();

        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: _host.FormHandle, IncludePopups: true));

        var names = snap.Interactive.Select(e => e.Name).ToList();
        names.Should().Contain(["MenuHostBtn", "CtxItemA", "CtxItemB", "CtxItemC"],
            "the checklist's done-when: an opened menu's items are in the observation");
        names.IndexOf("CtxItemA").Should().BeGreaterThan(names.IndexOf("MenuHostBtn"), "the window first, then its popups");
    }

    [Fact]
    public async Task Without_popups_the_menu_is_not_in_the_forms_snapshot()
    {
        HostIsUp();

        var snap = await _svc.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: _host.FormHandle));

        snap.Interactive.Select(e => e.Name).Should().Contain("MenuHostBtn").And.NotContain("CtxItemA",
            "the menu is its own top-level window, so only IncludePopups reaches it");
    }
}

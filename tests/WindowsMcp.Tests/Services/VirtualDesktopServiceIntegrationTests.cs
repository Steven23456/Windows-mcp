using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-12 phase 1 (R3): the <b>real</b> collaborators — the real <see cref="RegistryService"/> over
/// this machine's own keys and the real <c>IVirtualDesktopManager</c> COM object. Without these
/// the mocked tests in <see cref="VirtualDesktopServiceTests"/> would stay green even if the
/// service read the wrong key, mis-declared the COM vtable, or never created the manager at all
/// — the exact failure mode CLAUDE.md records for <c>disk_inspect mode:reclaimable</c>.
/// <para>
/// Read-only: nothing here writes to the registry or moves a window. Where a box legitimately
/// has no data (10.0.28000 has the VirtualDesktops key but no <c>VirtualDesktopIDs</c> value,
/// and a headless run has no windows) the test asserts the non-exceptional empty contract and
/// returns rather than failing on the environment.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class VirtualDesktopServiceIntegrationTests
{
    private static VirtualDesktopService NewService() => new(new RegistryService());

    private static readonly Regex GuidD = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled);

    // ---- the registry half -------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_reads_this_machines_registry_without_throwing()
    {
        var act = () => NewService().ListAsync();

        await act.Should().NotThrowAsync("an unusual or absent key layout is data, not an error");
        var desktops = await act();
        desktops.Should().NotBeNull();

        if (desktops.Length == 0)
            return;   // no VirtualDesktopIDs value on this build - the empty contract, already asserted

        desktops.Select(d => d.Index).Should().Equal(Enumerable.Range(0, desktops.Length),
            "Index is the position in the list");
        desktops.Select(d => d.Id).Should().OnlyHaveUniqueItems();
        desktops.Should().OnlyContain(d => GuidD.IsMatch(d.Id), "ids are lower-case dashed GUIDs");
        desktops.Should().OnlyContain(d => d.Name.Trim().Length > 0,
            "an unnamed desktop still gets its 'Desktop N' fallback");
        desktops.Count(d => d.IsCurrent).Should().BeLessThanOrEqualTo(1,
            "at most one desktop is the current one");
    }

    [Fact]
    public async Task GetCurrentAsync_agrees_with_the_entry_ListAsync_flagged()
    {
        var service = NewService();

        var all = await service.ListAsync();
        var current = await service.GetCurrentAsync();

        current.Should().Be(all.SingleOrDefault(d => d.IsCurrent),
            "the two calls are one truth: current is the flagged entry of the list, or null");
    }

    // ---- the COM half ------------------------------------------------------------------------

    [Fact]
    public async Task GetWindowDesktopIdAsync_of_hwnd_zero_is_null()
    {
        var id = await NewService().GetWindowDesktopIdAsync(0);

        id.Should().BeNull("0 is not a window; the COM failure is reported as 'unknown', not thrown");
    }

    [Fact]
    public async Task IsWindowOnCurrentDesktopAsync_of_hwnd_zero_is_null()
    {
        var on = await NewService().IsWindowOnCurrentDesktopAsync(0);

        on.Should().BeNull("neither true nor false is honest about a handle that is not a window");
    }

    [Fact]
    public async Task GetWindowDesktopIdAsync_of_a_handle_that_is_not_a_window_is_null()
    {
        // A handle value no window will have; E_FAIL comes back and must not escape as an exception.
        var act = () => NewService().GetWindowDesktopIdAsync(0x7FFF_FFF0);

        await act.Should().NotThrowAsync();
        (await act()).Should().BeNull();
    }

    [Fact]
    public async Task GetWindowDesktopIdAsync_of_a_real_window_is_one_of_the_listed_desktops()
    {
        var service = NewService();
        var window = await ForegroundOrFirstVisibleWindow();
        if (window is null)
            return;   // no interactive desktop in this run

        var id = await service.GetWindowDesktopIdAsync(window.Hwnd);

        id.Should().NotBeNull("the documented IVirtualDesktopManager answers for a live top-level window");
        id.Should().MatchRegex(GuidD.ToString());
        id.Should().NotBe(Guid.Empty.ToString("D"), "GUID_NULL means 'no desktop', which is reported as null");

        var all = await service.ListAsync();
        if (all.Length > 0)
            all.Select(d => d.Id).Should().Contain(id!,
                "a window's desktop is one of the desktops the registry lists");
    }

    [Fact]
    public async Task GetWindowDesktopIdAsync_is_stable_across_calls()
    {
        // Proves the lazily-created COM object survives being reused - a manager that was
        // released after the first call would come back null the second time.
        var service = NewService();
        var window = await ForegroundOrFirstVisibleWindow();
        if (window is null)
            return;

        var first = await service.GetWindowDesktopIdAsync(window.Hwnd);
        var second = await service.GetWindowDesktopIdAsync(window.Hwnd);

        second.Should().Be(first);
    }

    [Fact]
    public async Task IsWindowOnCurrentDesktopAsync_of_the_foreground_window_is_true()
    {
        var service = NewService();
        var active = await new WindowService().GetActiveAsync();
        if (active is null)
            return;   // no foreground window in this run

        var on = await service.IsWindowOnCurrentDesktopAsync(active.Hwnd);

        on.Should().BeTrue("the window the user is looking at is on the desktop the user is looking at");
    }

    private static async Task<WindowInfo?> ForegroundOrFirstVisibleWindow()
    {
        var service = new WindowService();
        var active = await service.GetActiveAsync();
        if (active is not null) return active;
        return (await service.ListAsync()).FirstOrDefault(w => w.State != WindowState.Minimized);
    }

    [Fact]
    public async Task GetCurrentAsync_falls_back_to_the_foreground_windows_desktop_when_the_registry_has_no_current_value()
    {
        // On this OS (10.0.28000) neither VirtualDesktops nor SessionInfo carries CurrentVirtualDesktop,
        // so the only way to know the current desktop is to ask which one the foreground window is on.
        var svc = new VirtualDesktopService(new RegistryService());
        var all = await svc.ListAsync();
        if (all.Length == 0) return;   // no desktops known to this box: nothing to be current
        if (await new WindowService().GetActiveAsync() is null)
            return;   // no foreground window in this run, so there is nothing to fall back to

        var current = await svc.GetCurrentAsync();

        current.Should().NotBeNull("a desktop session always has a foreground window, and it is on some desktop");
        all.Should().ContainSingle(d => d.IsCurrent).Which.Id.Should().Be(current!.Id);
    }

    // ---- GUID_NULL: the COM object answers, but the window is on no desktop --------------------

    [DllImport("user32.dll")]
    private static extern int EnumWindows(EnumWindowsProc callback, nint lParam);

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    /// <summary>
    /// The same documented interface the service declares - all three methods in vtable order
    /// (CLAUDE.md's COM rule) - so a test can read the <b>raw</b> answer the service translates.
    /// </summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    private interface IVirtualDesktopManagerProbe
    {
        void IsWindowOnCurrentVirtualDesktop(nint hwnd, out int onCurrentDesktop);
        void GetWindowDesktopId(nint hwnd, out Guid desktopId);
        void MoveWindowToDesktop(nint hwnd, in Guid desktopId);
    }

    /// <summary>
    /// Every top-level window the COM object will answer about, with the raw GUID it gave. The
    /// windows it refuses (E_FAIL - most of them) are the other tests' subject, not this one's.
    /// </summary>
    private static List<(nint Hwnd, Guid Id)> RawDesktopIds()
    {
        var found = new List<(nint, Guid)>();
        var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
        if (type is null || Activator.CreateInstance(type) is not IVirtualDesktopManagerProbe manager)
            return found;
        EnumWindows((hwnd, _) =>
        {
            try { manager.GetWindowDesktopId(hwnd, out var id); found.Add((hwnd, id)); }
            catch { /* this window has no answer at all */ }
            return true;
        }, 0);
        return found;
    }

    [Fact]
    public async Task GetWindowDesktopIdAsync_reports_a_window_whose_desktop_is_GUID_NULL_as_null()
    {
        // Measured on 10.0.28000: a handful of live top-level shell windows answer S_OK with an
        // all-zero desktop id. "On no desktop" has to reach the caller as null - as
        // "00000000-0000-0000-0000-000000000000" it would read like a real desktop in the JSON,
        // and would never match any entry of ListAsync.
        var noDesktop = RawDesktopIds().Where(w => w.Id == Guid.Empty).Select(w => w.Hwnd).ToList();
        if (noDesktop.Count == 0)
            return;   // no window in this session is on no desktop right now

        var service = NewService();

        foreach (var hwnd in noDesktop)
            (await service.GetWindowDesktopIdAsync(hwnd)).Should().BeNull(
                "GUID_NULL is the COM object saying 'no desktop', which the service reports as unknown");
    }

    [Fact]
    public async Task GetWindowDesktopIdAsync_returns_exactly_the_id_the_com_object_reported()
    {
        // The format conversion is the service's only liberty: same GUID, lower-case, dashed,
        // unbraced. Pinned against the raw COM answer, not against another copy of our own code.
        var onADesktop = RawDesktopIds().Where(w => w.Id != Guid.Empty).ToList();
        if (onADesktop.Count == 0)
            return;   // no interactive desktop in this run

        var service = NewService();

        foreach (var (hwnd, id) in onADesktop)
            (await service.GetWindowDesktopIdAsync(hwnd)).Should().Be(id.ToString("D").ToLowerInvariant(),
                "the service reformats the GUID it was given, it does not choose a different one");
    }

    /// <summary>
    /// The non-vacuity guard for every other test in this class. They all bail out when the list
    /// is empty, so deleting the sub-key fallback would leave them green on this build while the
    /// tool returned nothing - the <c>disk_inspect mode:reclaimable</c> failure mode CLAUDE.md
    /// records. This one reads the same key with the real RegistryService and insists the service
    /// found what is actually there.
    /// </summary>
    [Fact]
    public async Task ListAsync_finds_the_desktops_this_machines_registry_actually_holds()
    {
        const string desktopsKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops";
        var subKeys = await new RegistryService().EnumerateSubKeysAsync("HKCU", desktopsKey);
        var expected = subKeys.Where(n => Guid.TryParseExact(n, "B", out _))
            .Select(n => Guid.Parse(n).ToString("D").ToLowerInvariant()).ToArray();
        if (expected.Length == 0)
            return;   // this build keeps no per-desktop sub-keys: nothing real to check against

        var desktops = await NewService().ListAsync();

        desktops.Should().NotBeEmpty(
            "the registry holds {0} desktop sub-key(s) under {1}, so the service must report desktops",
            expected.Length, desktopsKey);
        desktops.Select(d => d.Id).Should().IntersectWith(expected,
            "the ids reported are the ones this machine's registry holds, not invented ones");
    }
}

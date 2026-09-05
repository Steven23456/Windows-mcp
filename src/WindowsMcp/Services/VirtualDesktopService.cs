using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-12 phase 1: the virtual-desktop inventory from the registry, and which desktop a window is
/// on from the documented <c>IVirtualDesktopManager</c>. Every failure — no registry key, a COM
/// call that refuses — is an empty answer or null, never a throw: the inventory is decoration on
/// the window list, not a reason it fails. The undocumented internal interface (create, switch,
/// rename) is deliberately not here.
/// </summary>
public sealed class VirtualDesktopService : IVirtualDesktopService
{
    private const string Hive = "HKCU";
    private const string VdPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const string DesktopsPath = VdPath + @"\Desktops";

    private readonly IRegistryService _registry;
    private readonly object _comGate = new();
    private IVirtualDesktopManager? _manager;
    private bool _managerFailed;

    public VirtualDesktopService(IRegistryService registry) => _registry = registry;

    public async Task<VirtualDesktopInfo[]> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var ids = await BinaryAsync(VdPath, "VirtualDesktopIDs", ct);
        if (ids is null || ids.Length < 16)
        {
            // Windows 11 (observed on build 28000) keeps only the per-desktop subkeys, not the
            // ordered blob: fall back to the subkey names, in the registry's enumeration order.
            ids = await IdsFromSubKeysAsync(ct);
        }
        if (ids is null || ids.Length < 16) return [];

        var current = await BinaryAsync(VdPath, "CurrentVirtualDesktop", ct)
            ?? await BinaryAsync($@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{SessionId()}\VirtualDesktops", "CurrentVirtualDesktop", ct)
            ?? CurrentFromForegroundWindow();

        var names = new Dictionary<Guid, string?>();
        for (int i = 0; i + 16 <= ids.Length; i += 16)
        {
            var guid = new Guid(ids.AsSpan(i, 16));
            names[guid] = await StringAsync($@"{DesktopsPath}\{VirtualDesktopRegistry.GuidKey(guid)}", "Name", ct);
        }
        return VirtualDesktopRegistry.Parse(ids, current, g => names.GetValueOrDefault(g));
    }

    public async Task<VirtualDesktopInfo?> GetCurrentAsync(CancellationToken ct = default)
        => (await ListAsync(ct)).FirstOrDefault(d => d.IsCurrent);

    public Task<string?> GetWindowDesktopIdAsync(long hwnd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (hwnd == 0) return Task.FromResult<string?>(null);
        var guid = DesktopOf((nint)hwnd);
        return Task.FromResult(guid is { } g && g != Guid.Empty ? VirtualDesktopRegistry.Id(g) : null);
    }

    public Task<bool?> IsWindowOnCurrentDesktopAsync(long hwnd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (hwnd == 0) return Task.FromResult<bool?>(null);
        try
        {
            var m = Manager();
            if (m is null) return Task.FromResult<bool?>(null);
            m.IsWindowOnCurrentVirtualDesktop((nint)hwnd, out var on);
            return Task.FromResult<bool?>(on != 0);
        }
        catch { return Task.FromResult<bool?>(null); }
    }

    // ---- registry reads: a missing key/value/type is "no data", never an exception -------------

    private async Task<byte[]?> BinaryAsync(string path, string value, CancellationToken ct)
    {
        try { return (await _registry.GetAsync(Hive, path, value, ct)).Data as byte[]; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<string?> StringAsync(string path, string value, CancellationToken ct)
    {
        try { return (await _registry.GetAsync(Hive, path, value, ct)).Data as string; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<byte[]?> IdsFromSubKeysAsync(CancellationToken ct)
    {
        try
        {
            var names = await _registry.EnumerateSubKeysAsync(Hive, DesktopsPath, ct);
            var bytes = new List<byte>();
            foreach (var name in names ?? [])
                if (Guid.TryParseExact(name, "B", out var g)) bytes.AddRange(g.ToByteArray());
            return bytes.Count == 0 ? null : bytes.ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static int SessionId()
    {
        try { return Process.GetCurrentProcess().SessionId; } catch { return 1; }
    }

    /// <summary>No registry record of the current desktop: the foreground window is on it.</summary>
    private byte[]? CurrentFromForegroundWindow()
    {
        try
        {
            var fg = PInvoke.GetForegroundWindow();
            if (fg.IsNull) return null;
            return DesktopOf(HwndPointer(fg)) is { } g && g != Guid.Empty ? g.ToByteArray() : null;
        }
        catch { return null; }
    }

    private static unsafe nint HwndPointer(Windows.Win32.Foundation.HWND h) => (nint)h.Value;

    // ---- COM: the documented IVirtualDesktopManager ---------------------------------------------

    private Guid? DesktopOf(nint hwnd)
    {
        try
        {
            var m = Manager();
            if (m is null) return null;
            m.GetWindowDesktopId(hwnd, out var id);
            return id;
        }
        catch { return null; }   // E_FAIL for a non-window, or no manager on this host
    }

    private IVirtualDesktopManager? Manager()
    {
        lock (_comGate)
        {
            if (_manager is not null || _managerFailed) return _manager;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
                _manager = type is null ? null : Activator.CreateInstance(type) as IVirtualDesktopManager;
            }
            catch { _manager = null; }
            _managerFailed = _manager is null;
            return _manager;
        }
    }

    /// <summary>
    /// The documented interface, every method declared in vtable order (CLAUDE.md's COM rule):
    /// <c>MoveWindowToDesktop</c> is never called in phase 1 but has to occupy its slot.
    /// </summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    private interface IVirtualDesktopManager
    {
        void IsWindowOnCurrentVirtualDesktop(nint hwnd, out int onCurrentDesktop);
        void GetWindowDesktopId(nint hwnd, out Guid desktopId);
        void MoveWindowToDesktop(nint hwnd, in Guid desktopId);
    }
}

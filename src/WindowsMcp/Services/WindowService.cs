using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class WindowService : IWindowService
{
    private const uint WM_CLOSE = 0x0010;
    private const uint MONITORINFOF_PRIMARY = 1u;

    public Task<WindowAction> ExecuteAsync(string action, string? title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("title is required for window action", nameof(title));

        HWND hwnd = PInvoke.FindWindow(null, title);
        bool found = hwnd != HWND.Null;

        if (found)
        {
            switch (action.ToLowerInvariant())
            {
                case "minimize":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MINIMIZE);
                    break;
                case "maximize":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_MAXIMIZE);
                    break;
                case "restore":
                    PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
                    break;
                case "close":
                    // CloseWindow() actually minimizes; PostMessage WM_CLOSE performs a real close.
                    PInvoke.PostMessage(hwnd, WM_CLOSE, default, default);
                    break;
                default:
                    throw new ArgumentException($"Unknown action '{action}'; expected minimize|maximize|restore|close");
            }
        }

        return Task.FromResult(new WindowAction(action, title, found));
    }

    public Task<bool> SwitchToAsync(string title, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        HWND hwnd = PInvoke.FindWindow(null, title);
        if (hwnd == HWND.Null)
            return Task.FromResult(false);

        bool ok = PInvoke.SetForegroundWindow(hwnd);
        return Task.FromResult(ok);
    }

    public Task<int> LaunchAsync(string appName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = appName,
            UseShellExecute = true
        };

        // Dispose our wrapper handle; the launched app keeps running independently.
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch '{appName}'");

        return Task.FromResult(process.Id);
    }

    public unsafe Task<MonitorInfo[]> EnumerateMonitorsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var monitors = new List<HMONITOR>();

        PInvoke.EnumDisplayMonitors(default, null,
            (hMonitor, hdcMonitor, lprcMonitor, lParam) =>
            {
                monitors.Add(hMonitor);
                return true;
            },
            default);

        var results = new List<MonitorInfo>();
        foreach (var handle in monitors)
        {
            var info = new MONITORINFO
            {
                cbSize = (uint)sizeof(MONITORINFO)
            };

            if (PInvoke.GetMonitorInfo(handle, ref info))
            {
                var rc = info.rcMonitor;
                bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;

                // Index = position in the returned array, not the enumeration counter: a failed
                // GetMonitorInfo must not leave a gap, because screenshot/ocr 'display' selects
                // by position and reports Index — the two numberings have to be one numbering.
                int position = results.Count;
                results.Add(new MonitorInfo(
                    position,
                    $"Monitor{position}",
                    rc.left,
                    rc.top,
                    rc.right - rc.left,
                    rc.bottom - rc.top,
                    isPrimary));
            }
        }

        return Task.FromResult(results.ToArray());
    }
}

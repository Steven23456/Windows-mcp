using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// The Win32 half both overlays share (A-14's flash, S-11's control indicator): a layered,
/// click-through, top-most, non-activating, untitled tool window - so A-1's <c>WindowFilter</c> drops
/// it and it never takes focus or a click - painted from a premultiplied Skia bitmap through
/// <c>UpdateLayeredWindow</c>. Every member must be called on the thread that owns the window.
/// </summary>
internal static class LayeredWindow
{
    private const int ErrorClassAlreadyExists = 1410;

    // Static: a window class outlives every instance that registered it, so its procedure must too.
    private static readonly WNDPROC Proc = (hwnd, msg, wParam, lParam) => PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    private static readonly object Gate = new();
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);

    private static unsafe HWND Topmost => new((void*)-1);   // HWND_TOPMOST

    /// <summary>A hidden overlay window of <paramref name="className"/>; <see cref="HWND.Null"/> when there is no window station.</summary>
    internal static unsafe HWND Create(string className)
    {
        if (!EnsureClass(className)) return HWND.Null;
        return PInvoke.CreateWindowEx(
            WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_TOPMOST
                | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
            className, "", WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, null, null, null);
    }

    /// <summary>
    /// Paints <paramref name="bitmap"/> (premultiplied BGRA, <paramref name="rect"/>'s size) into
    /// the window and moves it to <paramref name="rect"/>. Does not change whether it is shown.
    /// </summary>
    internal static unsafe bool Paint(HWND hwnd, SKBitmap bitmap, ScreenRegion rect)
    {
        var screenDc = PInvoke.GetDC(HWND.Null);
        if (screenDc.IsNull) return false;
        var memDc = PInvoke.CreateCompatibleDC(screenDc);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = rect.Width;
            bmi.bmiHeader.biHeight = -rect.Height;   // top-down, like Skia's rows
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;   // BI_RGB

            // The safe handle deletes the DIB when this scope ends — after UpdateLayeredWindow has copied it.
            using var dibHandle = PInvoke.CreateDIBSection(memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, out void* bits, null, 0);
            if (dibHandle.IsInvalid || bits is null) return false;
            var dib = new HBITMAP(dibHandle.DangerousGetHandle());
            long bytes = (long)rect.Width * rect.Height * 4;
            Buffer.MemoryCopy((void*)bitmap.GetPixels(), bits, bytes, bytes);

            var old = PInvoke.SelectObject(memDc, dib);
            try
            {
                var dst = new System.Drawing.Point(rect.X, rect.Y);
                var size = new SIZE { cx = rect.Width, cy = rect.Height };
                var src = new System.Drawing.Point(0, 0);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = (byte)PInvoke.AC_SRC_OVER,
                    SourceConstantAlpha = 255,
                    AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
                };
                return PInvoke.UpdateLayeredWindow(hwnd, HDC.Null, dst, size, memDc, src, new COLORREF(0), blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
            }
            finally
            {
                PInvoke.SelectObject(memDc, old);
            }
        }
        finally
        {
            PInvoke.DeleteDC(memDc);
            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    /// <summary>Shows the window top-most at <paramref name="rect"/> without activating it.</summary>
    internal static void ShowTopmost(HWND hwnd, ScreenRegion rect) =>
        PInvoke.SetWindowPos(hwnd, Topmost, rect.X, rect.Y, rect.Width, rect.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);

    /// <summary>Moves the window without resizing, re-ordering or activating it.</summary>
    internal static void Move(HWND hwnd, int x, int y) =>
        PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    private static HINSTANCE GetInstance() => new(PInvoke.GetModuleHandle((string?)null).DangerousGetHandle());

    private static unsafe bool EnsureClass(string className)
    {
        lock (Gate)
        {
            if (Registered.Contains(className)) return true;
            fixed (char* name = className)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),   // managed struct (delegate field): no sizeof
                    lpfnWndProc = Proc,
                    hInstance = GetInstance(),
                    lpszClassName = name,
                };
                // 0 = failed; a class this process already registered is fine.
                if (PInvoke.RegisterClassEx(in wc) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
                    return false;
            }
            Registered.Add(className);
            return true;
        }
    }
}

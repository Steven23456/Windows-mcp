using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.WinRT;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// A-10: screen capture through Windows.Graphics.Capture (the compositor's own frames), which
/// sees GPU-accelerated and DRM-protected surfaces that GDI's <c>CopyFromScreen</c> returns
/// black for. One <c>GraphicsCaptureItem</c> per monitor the requested rect touches; each frame
/// is copied to a CPU-readable staging texture and the overlapping part blitted into the result.
/// Every failure returns false so the caller can fall back to GDI.
/// </summary>
internal sealed class WgcCaptureBackend : IDisposable
{
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IidDxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrtDevice;
    private bool _failed;

    internal static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); } catch { return false; }
    }

    /// <summary>
    /// Captures <paramref name="rect"/> (virtual-desktop pixels) as a BGRA8888 premultiplied Skia
    /// bitmap, one monitor at a time. False when WGC is unavailable or any monitor refused.
    /// </summary>
    internal bool TryCapture(ScreenRegion rect, IReadOnlyList<MonitorInfo> monitors, out SKBitmap? bitmap)
    {
        bitmap = null;
        if (!IsSupported() || !EnsureDevice()) return false;

        var result = new SKBitmap(new SKImageInfo(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        bool any = false;
        try
        {
            foreach (var m in monitors)
            {
                var overlap = Intersect(rect, new ScreenRegion(m.X, m.Y, m.Width, m.Height));
                if (overlap is null) continue;
                if (!CaptureMonitor(m, overlap, rect, result)) { result.Dispose(); return false; }
                any = true;
            }
        }
        catch
        {
            result.Dispose();
            return false;
        }
        if (!any) { result.Dispose(); return false; }
        bitmap = result;
        return true;
    }

    private unsafe bool CaptureMonitor(MonitorInfo m, ScreenRegion overlap, ScreenRegion rect, SKBitmap into)
    {
        var hmon = PInvoke.MonitorFromPoint(new System.Drawing.Point(m.X + m.Width / 2, m.Y + m.Height / 2), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        if (hmon.IsNull) return false;

        var item = CreateItemForMonitor(hmon);
        if (item is null) return false;

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { /* older contract */ }

        using var arrived = new ManualResetEventSlim(false);
        pool.FrameArrived += (_, _) => arrived.Set();
        session.StartCapture();
        if (!arrived.Wait(FrameTimeout)) return false;

        using var frame = pool.TryGetNextFrame();
        if (frame is null) return false;

        return CopyFrame(frame, m, overlap, rect, into);
    }

    private unsafe bool CopyFrame(Direct3D11CaptureFrame frame, MonitorInfo m, ScreenRegion overlap, ScreenRegion rect, SKBitmap into)
    {
        // The projected surface is a managed wrapper (closed with the frame's other resources
        // below): ask CsWinRT for the native pointer, then QI for the DXGI access interface the
        // projection does not expose.
        using var surface = frame.Surface;
        var surfacePtr = WinRT.MarshalInspectable<object>.FromManaged(surface);
        IDirect3DDxgiInterfaceAccess access;
        try
        {
            var accessIid = IidDxgiAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in accessIid, out var accessPtr));
            try { access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr); }
            finally { Marshal.Release(accessPtr); }
        }
        finally { Marshal.Release(surfacePtr); }

        var iid = IidTexture2D;
        access.GetInterface(ref iid, out var texturePtr);
        var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texturePtr);
        Marshal.Release(texturePtr);
        Marshal.ReleaseComObject(access);

        texture.GetDesc(out var desc);
        var staging = desc;
        staging.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        staging.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        staging.BindFlags = 0;
        staging.MiscFlags = 0;
        _device!.CreateTexture2D(staging, null, out var stagingTexture);
        _context!.CopyResource(stagingTexture, texture);

        _context.Map(stagingTexture, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);
        try
        {
            // Frame pixels are monitor-local; the overlap is virtual-desktop. Copy row by row,
            // clamped to what the texture really holds in both axes: the monitor bounds and the
            // frame agree under PerMonitorV2, but a resolution change between the enumeration
            // and the capture (or a host that is not DPI-aware) must never read past a row.
            int srcX = overlap.X - m.X, srcY = overlap.Y - m.Y;
            int dstX = overlap.X - rect.X, dstY = overlap.Y - rect.Y;
            if (srcX < 0 || srcY < 0 || srcX >= desc.Width || srcY >= desc.Height) return false;
            int width = Math.Min(overlap.Width, (int)desc.Width - srcX);
            int height = Math.Min(overlap.Height, (int)desc.Height - srcY);
            int rowBytes = width * 4;
            byte* src = (byte*)mapped.pData;
            byte* dst = (byte*)into.GetPixels();
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    src + (srcY + y) * mapped.RowPitch + srcX * 4,
                    dst + (dstY + y) * into.RowBytes + dstX * 4,
                    rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(stagingTexture, 0);
            Marshal.ReleaseComObject(stagingTexture);
            Marshal.ReleaseComObject(texture);
        }
        return true;
    }

    private static unsafe GraphicsCaptureItem? CreateItemForMonitor(HMONITOR hmon)
    {
        object? factory = null;
        try
        {
            using var className = new HStringHandle("Windows.Graphics.Capture.GraphicsCaptureItem");
            var iid = IidInterop;
            PInvoke.RoGetActivationFactory(className.Value, &iid, out factory);
            if (factory is not IGraphicsCaptureItemInterop interop) return null;
            var itemIid = IidGraphicsCaptureItem;
            interop.CreateForMonitor((nint)hmon.Value, ref itemIid, out var abi);
            if (abi == IntPtr.Zero) return null;
            try { return GraphicsCaptureItem.FromAbi(abi); }
            finally { Marshal.Release(abi); }
        }
        catch { return null; }
        finally { if (factory is not null) Marshal.ReleaseComObject(factory); }
    }

    private bool EnsureDevice()
    {
        lock (_gate)
        {
            if (_winrtDevice is not null) return true;
            if (_failed) return false;
            try
            {
                CreateDevice();
                return true;
            }
            catch
            {
                _failed = true;
                return false;
            }
        }
    }

    private unsafe void CreateDevice()
    {
        var featureLevel = D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0;
        PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, 0, PInvoke.D3D11_SDK_VERSION,
            out var device, &featureLevel, out var context).ThrowOnFailure();
        _device = device;
        _context = context;

        var dxgi = (IDXGIDevice)device;
        PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable).ThrowOnFailure();
        var ptr = Marshal.GetIUnknownForObject(inspectable);
        try { _winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    private static ScreenRegion? Intersect(ScreenRegion a, ScreenRegion b)
    {
        int l = Math.Max(a.X, b.X), t = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.X + a.Width, b.X + b.Width), bt = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return r > l && bt > t ? new ScreenRegion(l, t, r - l, bt - t) : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _winrtDevice?.Dispose();
            _winrtDevice = null;
            if (_context is not null) Marshal.ReleaseComObject(_context);
            if (_device is not null) Marshal.ReleaseComObject(_device);
            _context = null;
            _device = null;
        }
    }

    /// <summary>An HSTRING for the lifetime of a using block.</summary>
    private sealed class HStringHandle : IDisposable
    {
        private readonly WindowsDeleteStringSafeHandle _handle;
        public HStringHandle(string s)
            => PInvoke.WindowsCreateString(s, (uint)s.Length, out _handle).ThrowOnFailure();
        public unsafe HSTRING Value => new((void*)_handle.DangerousGetHandle());
        public void Dispose() => _handle.Dispose();
    }

    // COM interop the projection does not expose — one method each, declared in vtable order.
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(nint window, ref Guid iid, out nint result);
        void CreateForMonitor(nint monitor, ref Guid iid, out nint result);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        void GetInterface(ref Guid iid, out nint result);
    }
}

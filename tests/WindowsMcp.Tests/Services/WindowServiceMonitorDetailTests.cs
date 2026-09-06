using System.Runtime.InteropServices;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-12 through the <b>real</b> monitor enumeration. <see cref="MonitorInfoTests"/> pins the DTO
/// and would stay green if <c>EnumerateMonitorsAsync</c> never filled a single one of the four new
/// fields — the mocked-collaborator failure mode CLAUDE.md records for
/// <c>disk_inspect mode:reclaimable</c>. Every assertion here is either an invariant that holds on
/// any desktop, or a comparison against the same Win32 fact read independently by the test.
/// <para>
/// Read-only and headless-safe, the same bracket as
/// <see cref="ScreenToolsMonitorInventoryTests"/>: a window station, no foreground window, no
/// input, no capture.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class WindowServiceMonitorDetailTests
{
    private const int SPI_GETWORKAREA = 0x0030;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    // The display half of DEVMODEW. Only dmSize is written by us; the rest is filled by the OS,
    // which is exactly what CS0649 warns about — narrowly suppressed, as AuthenticodeInspector
    // does for SYSLIB0057.
#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
#pragma warning restore CS0649

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(int uiAction, int uiParam, ref RECT pvParam, int fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static async Task<MonitorInfo[]> Monitors() => await new WindowService().EnumerateMonitorsAsync();

    [Fact]
    public async Task Every_monitor_reports_a_work_area_inside_its_bounds()
    {
        var monitors = await Monitors();
        monitors.Should().NotBeEmpty("this session has at least one display");

        foreach (var m in monitors)
        {
            m.WorkArea.Should().NotBeNull($"monitor {m.Index} must report rcWork, not null");
            var work = m.WorkArea!;
            work.X.Should().BeGreaterThanOrEqualTo(m.X, "the work area cannot start left of the monitor");
            work.Y.Should().BeGreaterThanOrEqualTo(m.Y);
            (work.X + work.Width).Should().BeLessThanOrEqualTo(m.X + m.Width);
            (work.Y + work.Height).Should().BeLessThanOrEqualTo(m.Y + m.Height);
            work.Width.Should().BeGreaterThan(0);
            work.Height.Should().BeGreaterThan(0);
            work.Height.Should().BeLessThanOrEqualTo(m.Height, "the taskbar can only take height, never add it");
        }
    }

    [Fact]
    public async Task Every_monitor_reports_a_dpi_of_at_least_96_and_a_scale_that_matches_it()
    {
        var monitors = await Monitors();

        foreach (var m in monitors)
        {
            m.EffectiveDpi.Should().BeGreaterThanOrEqualTo(96, $"monitor {m.Index}: 96 dpi is 100% scaling");
            m.Scale.Should().BeApproximately(m.EffectiveDpi / 96.0, 1e-9,
                "Scale is defined as EffectiveDpi/96 and must never be computed some other way");
        }
    }

    [Fact]
    public async Task Every_monitor_reports_one_of_the_four_orientations()
    {
        var monitors = await Monitors();

        monitors.Select(m => m.Orientation).Should().OnlyContain(o => o == 0 || o == 90 || o == 180 || o == 270,
            "the field is degrees, not the raw DMDO_ 0..3 enum");
    }

    [Fact]
    public async Task The_primary_monitors_work_area_is_the_one_Windows_reports()
    {
        // The cross-check that makes this file worth having: SPI_GETWORKAREA read here, in the
        // test, against what the service says. A service that invented a work area (bounds minus a
        // guessed taskbar height, say) fails this and passes every other assertion in the file.
        var rect = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0).Should().BeTrue("SPI_GETWORKAREA must succeed");

        var primary = (await Monitors()).Single(m => m.IsPrimary);

        primary.WorkArea.Should().Be(new Bounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    [Fact]
    public async Task The_primary_monitors_dpi_is_the_one_GetDpiForMonitor_reports()
    {
        var primary = (await Monitors()).Single(m => m.IsPrimary);
        var centre = new POINT { X = primary.X + primary.Width / 2, Y = primary.Y + primary.Height / 2 };
        var handle = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out uint dpiX, out _).Should().Be(0, "S_OK");

        primary.EffectiveDpi.Should().Be((int)dpiX,
            "the effective DPI is read from the monitor, not assumed to be 96 - a 150% display is 144");
        primary.Scale.Should().BeApproximately(dpiX / 96.0, 1e-9);
    }

    [Fact]
    public async Task The_primary_monitors_orientation_is_the_one_EnumDisplaySettings_reports()
    {
        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref devMode)
            .Should().BeTrue("the default display device must report its current settings");
        int expected = (int)devMode.dmDisplayOrientation * 90;   // DMDO_DEFAULT/90/180/270 = 0..3

        var primary = (await Monitors()).Single(m => m.IsPrimary);

        primary.Orientation.Should().Be(expected,
            "the orientation is read from the display device, not derived from the rect's shape");
    }

    [Fact]
    public async Task The_seven_original_fields_still_say_what_they_always_said()
    {
        // B-12 is additive: A-8's `display` selection reads Index and the rect, and nothing about
        // them may drift while the four new fields are being filled.
        var monitors = await Monitors();

        monitors.Select(m => m.Index).Should().Equal(Enumerable.Range(0, monitors.Length),
            "Index is still the position in the returned array");
        monitors.Should().ContainSingle(m => m.IsPrimary, "exactly one monitor is primary");
        monitors.Should().OnlyContain(m => m.Width > 0 && m.Height > 0);
        monitors.Should().OnlyContain(m => m.DeviceName.Length > 0);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Windows.Win32;
using Windows.Win32.UI.HiDpi;

namespace WindowsMcp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Per-monitor DPI awareness V2: screen geometry and screenshots use physical pixels.
        // Required for correct HiDPI behavior across multi-monitor setups.
        // Must be called before any window/screen API. Affects ScreenshotService default
        // region AND InputService coordinate normalization (both call GetSystemMetrics).
        // CsWin32 exposes DPI_AWARENESS_CONTEXT as a HANDLE-typed struct, not an enum;
        // the documented sentinel values (per Win32 docs / windef.h) are negative
        // integers cast to the handle type. -4 == PER_MONITOR_AWARE_V2.
        try
        {
            PInvoke.SetProcessDpiAwarenessContext(
                new DPI_AWARENESS_CONTEXT((nint)(-4)));
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 10 1703: fall back to per-monitor V1.
            PInvoke.SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
        }

        // CRITICAL: MCP stdio servers must log to stderr only. stdout is JSON-RPC.
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();   // source generator discovers [McpServerTool] methods

        await builder.Build().RunAsync();
        return 0;
    }
}

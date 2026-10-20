using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;
using WindowsMcp.Services;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// A-9 (R6): the process-level screenshot scale is parsed once by <see cref="ServerOptions"/> and
/// handed to the services as a record (roadmap C7 — no <c>Environment.GetEnvironmentVariable</c>
/// inside a service). Both transports go through <c>AddWindowsMcp</c>, so registering it there is
/// what makes the option apply to stdio and HTTP alike.
/// </summary>
[Trait("Category", "Unit")]
public class WindowsMcpHostTests
{
    private static ServiceProvider Build(ServerOptions options)
    {
        // Empty builder: no configuration providers, no environment scan — AddWindowsMcp only
        // ever touches builder.Services, and nothing here is resolved except the options record.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.AddWindowsMcp(options);
        return builder.Services.BuildServiceProvider();
    }

    [Fact]
    public void AddWindowsMcp_registers_the_screenshot_scale_from_the_server_options()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotScale = 0.5 });

        provider.GetRequiredService<ScreenshotOptions>().Scale.Should().Be(0.5);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_default_scale_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<ScreenshotOptions>().Scale.Should().Be(1.0);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_screenshot_options_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotScale = 0.25 });

        provider.GetRequiredService<ScreenshotOptions>()
            .Should().BeSameAs(provider.GetRequiredService<ScreenshotOptions>());
    }

    [Fact]
    public void ScreenshotOptions_Default_is_no_scaling()
    {
        ScreenshotOptions.Default.Scale.Should().Be(1.0);
    }

    // ---- BuildHttpApp argument guards ------------------------------------------------------
    // A-9 changed this method's signature (the configureServices seam). Its two guards run
    // before Kestrel is touched, so they are unit-testable and must stay that way: a misrouted
    // stdio options record, or a TLS thumbprint with no certificate, must fail loudly at build
    // time rather than start an endpoint that is not the one the operator asked for.

    [Fact]
    public void BuildHttpApp_rejects_a_stdio_options_record()
    {
        var act = () => WindowsMcpHost.BuildHttpApp(ServerOptions.Stdio, cert: null);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("options");
        ex.Message.Should().Contain("Http");
    }

    [Fact]
    public void BuildHttpApp_rejects_a_configured_thumbprint_with_no_certificate()
    {
        var options = new ServerOptions(
            TransportKind.Http, "127.0.0.1", 0,
            CertThumbprint: "0123456789ABCDEF0123456789ABCDEF01234567", ApiKey: "a-key-that-is-long-enough");

        var act = () => WindowsMcpHost.BuildHttpApp(options, cert: null);

        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.ParamName.Should().Be("cert");
        ex.Message.Should().Contain("certificate");
    }

    // ---- A-2 / A-4 (R1): the element budget crosses into the service layer the same way ------
    // ServerOptions is internal to the server assembly, so UiTreeOptions is the public record the
    // sealed UIAutomationService is constructed with (roadmap C7 — no Environment.GetEnvironmentVariable
    // inside a service). Registered in AddWindowsMcp, so stdio and HTTP get it alike.

    [Fact]
    public void AddWindowsMcp_registers_the_tree_budget_from_the_server_options()
    {
        using var provider = Build(ServerOptions.Stdio with { MaxTreeElements = 200 });

        provider.GetRequiredService<UiTreeOptions>().MaxElements.Should().Be(200);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_default_tree_budget_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<UiTreeOptions>().MaxElements.Should().Be(500);
    }

    [Fact]
    public void AddWindowsMcp_registers_the_tree_options_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio with { MaxTreeElements = 42 });

        provider.GetRequiredService<UiTreeOptions>()
            .Should().BeSameAs(provider.GetRequiredService<UiTreeOptions>());
    }

    // ---- A-14 (R1): the flash switch, the profiling switch, and the overlay singleton ---------
    // Both switches ride the SAME two options records the earlier items introduced (roadmap C7),
    // so nothing new crosses into the tool layer except the overlay itself. The overlay is
    // registered unconditionally: the TOOL gates on ScreenshotOptions.Flash, so a server started
    // with --flash off still resolves ScreenTools.

    [Fact]
    public void AddWindowsMcp_registers_the_flash_switch_from_the_server_options()
    {
        using var on = Build(ServerOptions.Stdio);
        using var off = Build(ServerOptions.Stdio with { Flash = false });

        on.GetRequiredService<ScreenshotOptions>().Flash.Should().BeTrue("the flash is on by default");
        off.GetRequiredService<ScreenshotOptions>().Flash.Should().BeFalse("--flash off must reach the tool layer");
    }

    [Fact]
    public void AddWindowsMcp_registers_the_profiling_switch_on_both_options_records()
    {
        using var provider = Build(ServerOptions.Stdio with { ProfileSnapshot = true });

        provider.GetRequiredService<ScreenshotOptions>().Profile
            .Should().BeTrue("--profile-snapshot profiles the capture stages too, not only the walk");
        provider.GetRequiredService<UiTreeOptions>().Profile
            .Should().BeTrue("--profile-snapshot profiles the snapshot walk");
    }

    [Fact]
    public void AddWindowsMcp_leaves_profiling_off_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<ScreenshotOptions>().Profile.Should().BeFalse();
        provider.GetRequiredService<UiTreeOptions>().Profile.Should().BeFalse();
    }

    [Fact]
    public void ScreenshotOptions_Default_flashes_and_does_not_profile()
    {
        ScreenshotOptions.Default.Flash.Should().BeTrue();
        ScreenshotOptions.Default.Profile.Should().BeFalse();
    }

    [Fact]
    public void UiTreeOptions_Default_does_not_profile()
    {
        UiTreeOptions.Default.MaxElements.Should().Be(500);
        UiTreeOptions.Default.Profile.Should().BeFalse();
    }

    [Fact]
    public void AddWindowsMcp_registers_the_flash_overlay_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio);

        var overlay = provider.GetRequiredService<IFlashOverlay>();
        overlay.Should().BeOfType<FlashOverlay>();
        overlay.Should().BeSameAs(provider.GetRequiredService<IFlashOverlay>(),
            "one overlay per process: two would fight over the same screen area");
    }

    // ---- A-12 phase 1 (R4): the virtual-desktop service --------------------------------------
    // WindowTools takes it as a constructor argument, so a missing registration is not a quietly
    // unfilled field — it is a DI failure on every `window` call, on both transports.

    [Fact]
    public void AddWindowsMcp_registers_the_virtual_desktop_service()
    {
        using var provider = Build(ServerOptions.Stdio);

        var desktops = provider.GetRequiredService<IVirtualDesktopService>();
        desktops.Should().BeOfType<VirtualDesktopService>();
        desktops.Should().BeSameAs(provider.GetRequiredService<IVirtualDesktopService>(),
            "one per process, like every other service here");
    }

    // ---- B-8: the app catalog -----------------------------------------------------------------
    // WindowService takes it as a constructor argument, so a missing registration is not a quietly
    // unfilled field - it is every launch(name) failing at the catalog with a null reference.

    [Fact]
    public void AddWindowsMcp_registers_the_app_catalog_service()
    {
        using var provider = Build(ServerOptions.Stdio);

        var catalog = provider.GetRequiredService<IAppCatalogService>();
        catalog.Should().BeOfType<AppCatalogService>();
        catalog.Should().BeSameAs(provider.GetRequiredService<IAppCatalogService>(),
            "one per process - the five-minute cache is worthless if every call gets a new instance");
    }

    [Fact]
    public void AddWindowsMcp_gives_the_window_service_the_catalog_it_was_registered_with()
    {
        // B-8 launches by name through IAppCatalogService; a WindowService built without it would
        // compile, resolve, and then fail on the first launch("calc").
        using var provider = Build(ServerOptions.Stdio);

        var window = provider.GetRequiredService<IWindowService>();

        window.Should().BeOfType<WindowService>();
        var field = typeof(WindowService)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(f => f.FieldType == typeof(IAppCatalogService));
        field.Should().NotBeNull("the service has to hold the catalog it was given");
        field!.GetValue(window).Should().BeSameAs(provider.GetRequiredService<IAppCatalogService>());
    }

    [Fact]
    public void AddWindowsMcp_still_resolves_the_window_service_and_its_tool()
    {
        // WindowService gained an optional IVirtualDesktopService parameter and WindowTools a
        // required one: both must still come out of the container.
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<IWindowService>().Should().BeOfType<WindowService>();
        ActivatorUtilities.CreateInstance<WindowsMcp.Tools.WindowTools>(provider).Should().NotBeNull();
    }

    [Fact]
    public void AddWindowsMcp_registers_the_flash_overlay_even_when_the_flash_is_off()
    {
        // The tool gates on ScreenshotOptions.Flash; the registration is unconditional so that
        // ScreenTools still resolves - a missing registration would fail every screenshot with a
        // DI error instead of quietly not flashing.
        using var provider = Build(ServerOptions.Stdio with { Flash = false });

        provider.GetService<IFlashOverlay>().Should().NotBeNull();
    }
    // ---- C-5 (R8): the transport fact the scrape tool needs -----------------------------------
    // Sampling is a server->client request, so it needs a session. BuildHttpApp runs Streamable
    // HTTP stateless by an earlier decision, and a per-request server has no session; the tool has
    // to be told, because it cannot ask the transport. Registered here so both modes get the truth
    // about themselves from the one place they share.

    [Fact]
    public void AddWindowsMcp_registers_a_session_keeping_transport_for_stdio()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<TransportOptions>().Stateless.Should().BeFalse(
            "stdio keeps one session for the life of the process, so sampling can cross it");
    }

    [Fact]
    public void AddWindowsMcp_registers_a_stateless_transport_for_http()
    {
        using var provider = Build(ServerOptions.Stdio with { Transport = TransportKind.Http });

        provider.GetRequiredService<TransportOptions>().Stateless.Should().BeTrue(
            "BuildHttpApp sets Stateless on the Streamable HTTP transport, so there is no session "
            + "for a sampling reply to come back on");
    }

    [Fact]
    public void AddWindowsMcp_registers_the_transport_options_as_a_singleton()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<TransportOptions>()
            .Should().BeSameAs(provider.GetRequiredService<TransportOptions>());
    }

    [Fact]
    public void TransportOptions_Stdio_keeps_its_session()
        => TransportOptions.Stdio.Stateless.Should().BeFalse(
            "the default a tool built without an injected record assumes is the session-keeping one");

    /// <summary>
    /// C-5: <c>WebTools</c>'s <c>TransportOptions</c> parameter is optional so the unit tests can
    /// omit it — which means a missing registration would not fail DI, it would silently make every
    /// HTTP <c>summarize:true</c> ask a client that can never answer. This asserts the resolved tool
    /// holds the record the container was configured with.
    /// </summary>
    [Theory]
    [InlineData(false, false)]   // stdio: a session that lasts the process
    [InlineData(true, true)]     // http:  a fresh server per request
    public void AddWindowsMcp_gives_the_web_tool_the_transport_it_registered(bool http, bool stateless)
    {
        using var provider = Build(ServerOptions.Stdio with
        {
            Transport = http ? TransportKind.Http : TransportKind.Stdio,
        });

        var tools = ActivatorUtilities.CreateInstance<WindowsMcp.Tools.WebTools>(provider);

        var field = typeof(WindowsMcp.Tools.WebTools)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SingleOrDefault(f => f.FieldType == typeof(TransportOptions));
        field.Should().NotBeNull("the tool has to hold the transport it was given, not re-read it per call");
        field!.GetValue(tools).Should().BeSameAs(provider.GetRequiredService<TransportOptions>());
        ((TransportOptions)field.GetValue(tools)!).Stateless.Should().Be(stateless);
    }

    // ---- A-10 (R1/R6): the capture backend crosses into the tool layer, and the service is owned --
    // The backend rides the SAME ScreenshotOptions record the earlier items introduced (roadmap C7).
    // What is new is that ScreenshotService now holds a D3D device, so the container has to be the
    // thing that releases it: a singleton is only disposed if it is IDisposable.

    [Fact]
    public void AddWindowsMcp_registers_the_screenshot_backend_from_the_server_options()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "wgc" });

        provider.GetRequiredService<ScreenshotOptions>().Backend.Should().Be("wgc",
            "--screenshot-backend must reach the service layer, not be re-read from the environment");
    }

    [Fact]
    public void AddWindowsMcp_registers_the_default_backend_when_none_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);

        provider.GetRequiredService<ScreenshotOptions>().Backend.Should().Be("auto");
    }

    [Fact]
    public void ScreenshotOptions_Default_prefers_the_compositor()
    {
        ScreenshotOptions.Default.Backend.Should().Be("auto");
    }

    [Fact]
    public void AddWindowsMcp_resolves_the_screenshot_service_with_the_process_options()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "gdi" });

        var service = provider.GetRequiredService<IScreenshotService>();

        service.Should().BeOfType<ScreenshotService>();
        service.Should().BeSameAs(provider.GetRequiredService<IScreenshotService>(),
            "one capture service per process: two would each hold their own D3D device");
        service.Should().BeAssignableTo<IDisposable>(
            "the container only disposes a singleton that says it needs disposing");
        provider.GetRequiredService<ScreenshotOptions>().Backend.Should().Be("gdi",
            "and the options the service is constructed with are the ones the command line produced");
    }

    [Fact]
    public void Disposing_the_container_disposes_the_screenshot_service()
    {
        var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "wgc" });
        var service = provider.GetRequiredService<IScreenshotService>();

        var act = provider.Dispose;

        act.Should().NotThrow("shutting the server down releases the capture device, even if it was never created");
        service.Should().NotBeNull();
    }

    /// <summary>
    /// A-10 (R6): the registered record is not the point — the service HONOURING it is. With the
    /// process default at <c>wgc</c>, a call that says <c>auto</c> must refuse when the compositor
    /// cannot serve, instead of quietly taking a GDI frame. If the options never reached the
    /// constructor the resolved backend would be <c>auto</c> and this would fall back to GDI, so
    /// the assertion fails on the exact wiring mistake the record alone cannot catch.
    /// </summary>
    [Fact]
    public async Task The_resolved_screenshot_service_honours_the_configured_backend()
    {
        using var provider = Build(ServerOptions.Stdio with { ScreenshotBackend = "wgc" });
        var service = (ScreenshotService)provider.GetRequiredService<IScreenshotService>();
        service.WgcFrameSource = _ => null;   // the compositor refuses; no desktop is touched

        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 16, 16), new CaptureOptions());

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("wgc", "--screenshot-backend wgc reached the service that captures");
    }

    /// <summary>A-10 (R6): and the unconfigured server still falls back rather than refusing.</summary>
    [Fact]
    public async Task The_resolved_screenshot_service_falls_back_when_no_backend_was_configured()
    {
        using var provider = Build(ServerOptions.Stdio);
        var service = (ScreenshotService)provider.GetRequiredService<IScreenshotService>();
        service.WgcFrameSource = _ => null;

        // 0x0 is the tripwire that proves the GDI frame path was reached without needing a desktop.
        Func<Task> act = () => service.CaptureAsync(new ScreenRegion(0, 0, 0, 0), new CaptureOptions());

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().NotContain("wgc",
            "the default is auto: a refusing compositor is answered with GDI, silently");
    }

    // ---- HTTP log timestamps (fix/http-log-timestamps) ---------------------------------------
    // User report: "The datetime isn't showing up in the console logs when running the app in http
    // mode." ConfigureStderrLogging never set a timestamp format, and SimpleConsoleFormatterOptions
    // defaults it to null. HTTP mode gets local time with its UTC offset and milliseconds; stdio
    // stays bare because the MCP host (Claude Desktop/Code) prefixes its own timestamp to every
    // stderr line it captures, so a second one would double it.
    //
    // Two traps make an options-only assertion insufficient (both verified against the .NET 10
    // logging stack before these tests were written):
    //  * SimpleConsoleFormatter reads the UNNAMED SimpleConsoleFormatterOptions (CurrentValue).
    //    Options configured under the name ConsoleFormatterNames.Simple ("simple") are never read.
    //  * While ConsoleLoggerOptions.FormatterName is null - which is what AddConsole(o => ...)
    //    leaves it - ConsoleLoggerProvider OVERWRITES the simple formatter's options on construction
    //    from the obsolete ConsoleLoggerOptions.TimestampFormat (null). Configure<SimpleConsoleFormatterOptions>
    //    beside the existing AddConsole call therefore shows the format in the options and still
    //    renders no timestamp. Only a FormatterName that names the simple formatter makes it stick.
    // So each mode has an options assertion (the exact contract) AND a rendered-line assertion
    // through the formatter the real provider selected (the behaviour).
    //
    // Review round 2 (F2), policy "operator config wins where set": the HTTP default fills only
    // what the Logging:Console configuration leaves unset. A configured FormatterName (json,
    // systemd), FormatterOptions:TimestampFormat or FormatterOptions:UseUtcTimestamp is honoured;
    // LogToStandardErrorThreshold stays Trace whatever the config says, because stdout is the
    // JSON-RPC channel in stdio and the same sink is kept in HTTP. HTTP mode no longer honours the
    // deprecated flat ConsoleLoggerOptions settings (obsolete since .NET 5) - Logging:Console:Format,
    // TimestampFormat, IncludeScopes, DisableColors and UseUtcTimestamp - because the provider
    // applies them only while FormatterName is null, and HTTP mode now names the simple formatter
    // (stdio leaves it null, so they still apply there). Accepted, and deliberately not pinned.
    // Those rows build the logging pipeline the way CreateBuilder/CreateApplicationBuilder do
    // (AddConfiguration over the Logging section, then AddConsole) from in-memory config, so they
    // are hermetic: the process environment is never read or written.

    /// <summary>The HTTP-mode stamp: local time, milliseconds, UTC offset, and the separating space.</summary>
    internal const string HttpTimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";

    /// <summary>A rendered HTTP-mode first line: the timestamp, exactly one space, then the level token.</summary>
    private static readonly Regex TimestampedLine = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) info: ", RegexOptions.CultureInvariant);

    private static readonly Regex AnsiColor = new(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant);

    internal const string SdkCategory = "ModelContextProtocol.Server.McpServer";
    internal const string OwnCategory = "WindowsMcp.Tools.Probe";

    private static ServiceProvider BuildLogging(bool http) =>
        new ServiceCollection()
            .AddLogging(logging => WindowsMcpHost.ConfigureStderrLogging(logging, http))
            .BuildServiceProvider();

    /// <summary>
    /// Renders one Information line through the console formatter the real
    /// <see cref="ConsoleLoggerProvider"/> uses and returns its first line, ANSI colour codes
    /// stripped. Public API only, no mocks: the logger factory is resolved first because building
    /// the provider is when it pushes the obsolete <see cref="ConsoleLoggerOptions"/> into the simple
    /// formatter (no formatter name set); the formatter resolved afterwards is the same DI
    /// singleton the provider holds. Invariant culture because ':' in a .NET custom date format is
    /// the culture's time separator, which would make the shape assertion machine-dependent.
    /// The pin is test-only: the server's stamp deliberately follows the machine's CurrentCulture -
    /// its calendar (th-TH writes 2569-...) as well as its time separator - exactly as
    /// SimpleConsoleFormatter does for every ASP.NET Core app (review round 2, F1: accepted).
    /// </summary>
    internal static string RenderFirstLine(IServiceProvider services)
    {
        _ = services.GetRequiredService<ILoggerFactory>();
        services.GetServices<ILoggerProvider>().OfType<ConsoleLoggerProvider>()
            .Should().ContainSingle("logs reach stderr through exactly one console provider");

        // The provider's own selection: the named formatter, else the simple one.
        var name = services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName
            ?? ConsoleFormatterNames.Simple;
        var formatter = services.GetServices<ConsoleFormatter>().Single(f => f.Name == name);

        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            var entry = new LogEntry<string>(
                LogLevel.Information, OwnCategory, new EventId(0), "probe message", null, (state, _) => state);
            formatter.Write(in entry, null, writer);
            var firstLine = writer.ToString().Split('\n')[0].TrimEnd('\r');
            return AnsiColor.Replace(firstLine, string.Empty);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>
    /// <paramref name="line"/> starts with a local, millisecond, offset-carrying stamp for the
    /// moment it was rendered (between <paramref name="before"/> and <paramref name="after"/>),
    /// immediately followed by one space and the level token.
    /// </summary>
    internal static void ShouldStartWithALocalTimestampForNow(string line, DateTimeOffset before, DateTimeOffset after)
    {
        var match = TimestampedLine.Match(line);
        match.Success.Should().BeTrue(
            "an HTTP-mode line must start '<yyyy-MM-dd HH:mm:ss.fff zzz> info: ' but rendered \"{0}\"; if "
            + "SimpleConsoleFormatterOptions.TimestampFormat is set and this still fails, the console provider "
            + "overwrote it because ConsoleLoggerOptions.FormatterName does not name the simple formatter "
            + "(AddSimpleConsole sets it)", line);

        var stamp = DateTimeOffset.ParseExact(
            match.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        stamp.Should().BeOnOrAfter(before.AddSeconds(-2)).And.BeOnOrBefore(after.AddSeconds(2),
            "the stamp is the wall-clock moment the line was written, on a 24-hour clock");
        stamp.Offset.Should().Be(TimeZoneInfo.Local.GetUtcOffset(stamp.UtcDateTime),
            "the stamp is local time carrying its UTC offset, not UTC (UseUtcTimestamp stays false)");
    }

    [Fact]
    public void ConfigureStderrLogging_http_sets_a_local_millisecond_timestamp_with_offset()
    {
        using var provider = BuildLogging(http: true);

        var simple = provider.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;

        simple.TimestampFormat.Should().Be(HttpTimestampFormat,
            "nothing prefixes stderr in HTTP mode, so each line carries its own local time, offset and "
            + "milliseconds; the trailing space separates the stamp from the level token written next");
        simple.UseUtcTimestamp.Should().BeFalse("the stamp is local time; zzz carries the offset");
    }

    [Fact]
    public void ConfigureStderrLogging_http_renders_each_line_with_a_leading_local_timestamp()
    {
        using var provider = BuildLogging(http: true);

        var before = DateTimeOffset.Now;
        var line = RenderFirstLine(provider);
        var after = DateTimeOffset.Now;

        ShouldStartWithALocalTimestampForNow(line, before, after);
    }

    [Fact]
    public void ConfigureStderrLogging_stdio_leaves_the_timestamp_off()
    {
        using var provider = BuildLogging(http: false);

        provider.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue.TimestampFormat
            .Should().BeNull("the MCP host prefixes its own timestamp to every stderr line it captures; a second would double it");
    }

    [Fact]
    public void ConfigureStderrLogging_stdio_renders_lines_without_a_timestamp()
    {
        using var provider = BuildLogging(http: false);

        var line = RenderFirstLine(provider);

        line.Should().StartWith("info: ", "a stdio line starts at the level token; the MCP host adds the time");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureStderrLogging_routes_every_level_to_stderr(bool http)
    {
        using var provider = BuildLogging(http);

        provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.LogToStandardErrorThreshold
            .Should().Be(LogLevel.Trace,
                "stdout is the JSON-RPC channel in stdio mode, and HTTP mode keeps the same sink");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigureStderrLogging_logs_Information_and_above(bool http)
    {
        using var provider = BuildLogging(http);
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(OwnCategory);

        logger.IsEnabled(LogLevel.Debug).Should().BeFalse("the minimum level is Information");
        logger.IsEnabled(LogLevel.Information).Should().BeTrue("the minimum level is Information");
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue();
    }

    [Fact]
    public void ConfigureStderrLogging_http_quiets_the_mcp_sdk_below_Warning()
    {
        using var provider = BuildLogging(http: true);
        var sdk = provider.GetRequiredService<ILoggerFactory>().CreateLogger(SdkCategory);

        sdk.IsEnabled(LogLevel.Information).Should().BeFalse(
            "stateless HTTP builds a server per request, so the SDK's lifecycle chatter would repeat on every call");
        sdk.IsEnabled(LogLevel.Warning).Should().BeTrue("SDK warnings and errors still reach the operator");
    }

    [Fact]
    public void ConfigureStderrLogging_stdio_does_not_quiet_the_mcp_sdk()
    {
        using var provider = BuildLogging(http: false);
        var sdk = provider.GetRequiredService<ILoggerFactory>().CreateLogger(SdkCategory);

        sdk.IsEnabled(LogLevel.Information).Should().BeTrue(
            "the Warning filter is HTTP-only: stdio runs one server for the life of the process");
    }

    // ---- F2: operator Logging:Console configuration wins where set -------------------------

    /// <summary>
    /// The logging pipeline as <c>WebApplication.CreateBuilder</c> / <c>Host.CreateApplicationBuilder</c>
    /// leave it before our call - <c>AddConfiguration(Logging section)</c>, then <c>AddConsole()</c> -
    /// fed from in-memory settings (keys relative to <c>Logging</c>), then
    /// <see cref="WindowsMcpHost.ConfigureStderrLogging"/>. Hermetic: no environment, no files.
    /// </summary>
    private static ServiceProvider BuildLoggingWithConfig(bool http, params (string Key, string Value)[] logging)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(logging.Select(s => new KeyValuePair<string, string?>("Logging:" + s.Key, s.Value)))
            .Build();

        return new ServiceCollection()
            .AddLogging(builder =>
            {
                builder.AddConfiguration(config.GetSection("Logging"));
                builder.AddConsole();
                WindowsMcpHost.ConfigureStderrLogging(builder, http);
            })
            .BuildServiceProvider();
    }

    private static string SelectedFormatterName(IServiceProvider services) =>
        services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName
            ?? ConsoleFormatterNames.Simple;

    [Fact]
    public void ConfigureStderrLogging_http_with_an_empty_Logging_section_keeps_the_local_millisecond_stamp()
    {
        using var provider = BuildLoggingWithConfig(http: true);

        var simple = provider.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;
        simple.TimestampFormat.Should().Be(HttpTimestampFormat, "nothing is configured, so the HTTP default applies");
        simple.UseUtcTimestamp.Should().BeFalse("nothing is configured, so the stamp is local time");
        SelectedFormatterName(provider).Should().Be(ConsoleFormatterNames.Simple);

        var before = DateTimeOffset.Now;
        var line = RenderFirstLine(provider);
        var after = DateTimeOffset.Now;
        ShouldStartWithALocalTimestampForNow(line, before, after);
    }

    [Fact]
    public void ConfigureStderrLogging_stdio_with_an_empty_Logging_section_stays_unstamped()
    {
        using var provider = BuildLoggingWithConfig(http: false);

        RenderFirstLine(provider).Should().StartWith("info: ",
            "stdio is unchanged by the HTTP default: the MCP host adds the time");
    }

    [Fact]
    public void ConfigureStderrLogging_http_honours_a_configured_json_formatter()
    {
        using var provider = BuildLoggingWithConfig(http: true, ("Console:FormatterName", "json"));

        SelectedFormatterName(provider).Should().Be(ConsoleFormatterNames.Json,
            "an operator who configured Logging:Console:FormatterName=json gets JSON; the HTTP default only fills what is unset");

        var line = RenderFirstLine(provider);
        line.Should().StartWith("{", "a json-formatted entry is one JSON object, not '<stamp> info: ...'");
        using var json = JsonDocument.Parse(line);
        json.RootElement.GetProperty("Message").GetString().Should().Be("probe message");
        json.RootElement.GetProperty("LogLevel").GetString().Should().Be("Information");
    }

    [Fact]
    public void ConfigureStderrLogging_http_honours_a_configured_systemd_formatter()
    {
        using var provider = BuildLoggingWithConfig(http: true, ("Console:FormatterName", "systemd"));

        SelectedFormatterName(provider).Should().Be(ConsoleFormatterNames.Systemd,
            "a configured formatter name wins over the HTTP default's 'simple'");
        RenderFirstLine(provider).Should().StartWith("<6>",
            "systemd renders an Information entry with its syslog priority prefix, not the HTTP stamp");
    }

    [Fact]
    public void ConfigureStderrLogging_http_honours_a_configured_timestamp_format()
    {
        using var provider = BuildLoggingWithConfig(http: true, ("Console:FormatterOptions:TimestampFormat", "[HH:mm:ss] "));

        provider.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue.TimestampFormat
            .Should().Be("[HH:mm:ss] ",
                "an operator who configured Logging:Console:FormatterOptions:TimestampFormat gets that format, not the HTTP default");

        RenderFirstLine(provider).Should().MatchRegex(@"^\[\d{2}:\d{2}:\d{2}\] info: ",
            "the configured format is what the simple formatter actually writes");
    }

    [Fact]
    public void ConfigureStderrLogging_http_with_a_configured_simple_formatter_and_no_format_still_stamps()
    {
        using var provider = BuildLoggingWithConfig(http: true, ("Console:FormatterName", "simple"));

        provider.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue.TimestampFormat
            .Should().Be(HttpTimestampFormat, "naming the simple formatter leaves the timestamp unset, so the HTTP default fills it");

        var before = DateTimeOffset.Now;
        var line = RenderFirstLine(provider);
        var after = DateTimeOffset.Now;
        ShouldStartWithALocalTimestampForNow(line, before, after);
    }

    [Fact]
    public void ConfigureStderrLogging_http_honours_a_configured_UseUtcTimestamp_with_the_default_format()
    {
        using var provider = BuildLoggingWithConfig(http: true, ("Console:FormatterOptions:UseUtcTimestamp", "true"));

        var simple = provider.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;
        simple.UseUtcTimestamp.Should().BeTrue("a configured UseUtcTimestamp is honoured");
        simple.TimestampFormat.Should().Be(HttpTimestampFormat, "only the clock was configured; the format is still the HTTP default");

        var before = DateTimeOffset.UtcNow;
        var line = RenderFirstLine(provider);
        var after = DateTimeOffset.UtcNow;
        var match = TimestampedLine.Match(line);
        match.Success.Should().BeTrue("the entry still starts with the HTTP stamp, but rendered \"{0}\"", line);
        var stamp = DateTimeOffset.ParseExact(match.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        stamp.Offset.Should().Be(TimeSpan.Zero, "UseUtcTimestamp=true renders UTC, which zzz shows as +00:00");
        stamp.Should().BeOnOrAfter(before.AddSeconds(-2)).And.BeOnOrBefore(after.AddSeconds(2));
    }

    [Theory]
    [InlineData(false, "None")]
    [InlineData(true, "None")]
    [InlineData(false, "Critical")]
    [InlineData(true, "Critical")]
    public void ConfigureStderrLogging_keeps_every_level_on_stderr_whatever_the_config_says(bool http, string configured)
    {
        using var provider = BuildLoggingWithConfig(http, ("Console:LogToStandardErrorThreshold", configured));

        provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.LogToStandardErrorThreshold
            .Should().Be(LogLevel.Trace,
                "a configured threshold would send log lines to stdout - the JSON-RPC channel in stdio - so this one setting is not the operator's");
    }

    // ---- F3: the plain-HTTP warning is a logged entry, not a raw stderr write --------------

    [Fact]
    public void Program_does_not_write_the_plain_HTTP_warning_straight_to_stderr()
    {
        // A source guard (the behaviour itself is HttpHostLoggingIntegrationTests: the warning is
        // logged by BuildHttpApp). An off-loopback bind cannot be exercised end to end unattended -
        // the first listen on a public interface raises a firewall prompt - so this is what stops
        // the unstamped Console.Error write from coming back beside the logged one.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Windows-mcp.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the guard reads src/WindowsMcp/Program.cs, so it must run from inside the repo");

        var program = File.ReadAllText(Path.Combine(dir!.FullName, "src", "WindowsMcp", "Program.cs"));

        var rawWrite = Regex.Match(program, @"Console\.Error\.Write(Line)?\([^;]*plain HTTP[^;]*;");
        rawWrite.Success.Should().BeFalse(
            "the plain-HTTP warning must go through the logger so it carries the HTTP-mode timestamp like every other entry, "
            + "but Program.cs still writes it raw: {0}", rawWrite.Value);
    }
}

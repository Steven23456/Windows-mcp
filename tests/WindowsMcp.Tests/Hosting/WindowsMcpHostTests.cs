using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;

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
}

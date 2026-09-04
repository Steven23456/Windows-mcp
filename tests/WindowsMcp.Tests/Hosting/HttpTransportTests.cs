using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Moq;
using ModelContextProtocol.Protocol;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Hosting;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// Starts the real HTTP host (<see cref="WindowsMcpHost.BuildHttpApp"/>) in-process on an
/// ephemeral loopback port and talks to it with the SDK's own client. This is the only test that
/// exercises the service/tool wiring both transports share end-to-end — nothing drives the stdio
/// host in-process.
/// </summary>
[Trait("Category", "Integration")]
public class HttpTransportTests
{
    private const string ApiKey = "integration-test-api-key-0123";

    // ---- harness --------------------------------------------------------------------------

    internal sealed class Harness : IAsyncDisposable
    {
        public WebApplication App { get; }
        public Uri BaseAddress { get; }
        public Uri McpEndpoint => new(BaseAddress, WindowsMcpHost.McpPath);

        private Harness(WebApplication app, Uri baseAddress)
        {
            App = app;
            BaseAddress = baseAddress;
        }

        /// <param name="configureServices">
        /// Runs after <c>AddWindowsMcp</c>, so a registration here replaces the real service
        /// (A-7 follow-up: without this seam every transport test of the screenshot surface has
        /// to capture the real screen, which no headless run can do).
        /// </param>
        public static async Task<Harness> StartAsync(
            string? apiKey = null,
            X509Certificate2? cert = null,
            Action<IServiceCollection>? configureServices = null)
        {
            // Port 0: Kestrel picks a free port and reports it via IServerAddressesFeature —
            // no pick-then-bind race.
            var options = new ServerOptions(TransportKind.Http, "127.0.0.1", 0, cert?.Thumbprint, apiKey);
            var app = WindowsMcpHost.BuildHttpApp(options, cert, configureServices);
            await app.StartAsync();

            var address = WindowsMcpHost.GetListeningAddress(app)
                ?? throw new InvalidOperationException("Kestrel reported no listening address");
            return new Harness(app, new Uri(address));
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    internal static async Task<McpClient> ConnectAsync(Uri endpoint, string? apiKey = null, HttpClient? httpClient = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,   // pin: AutoDetect would mask a Streamable HTTP regression by falling back to SSE
            AdditionalHeaders = apiKey is null
                ? null
                : new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" },
        };

        var transport = httpClient is null
            ? new HttpClientTransport(options)
            : new HttpClientTransport(options, httpClient);

        return await McpClient.CreateAsync(transport);
    }

    private static StringContent InitializeBody() => new(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"0"}}}""",
        Encoding.UTF8,
        "application/json");

    private static string FileWriteToolName(IEnumerable<McpClientTool> tools) =>
        tools.Single(t => t.Name.Replace("_", "").Equals("filewrite", StringComparison.OrdinalIgnoreCase)).Name;

    internal static string ScreenshotToolName(IEnumerable<McpClientTool> tools) =>
        tools.Single(t => t.Name.Replace("_", "").Equals("screenshot", StringComparison.OrdinalIgnoreCase)).Name;

    /// <summary>
    /// A throwaway localhost server certificate. SChannel cannot serve TLS with the purely
    /// in-memory key CreateSelfSigned produces, so it is round-tripped through PKCS#12.
    /// </summary>
    private static X509Certificate2 CreateEphemeralServerCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12), password: null, X509KeyStorageFlags.Exportable);
    }

    // ---- tests ----------------------------------------------------------------------------

    [Fact]
    public async Task Handshake_and_tool_listing_work_over_plain_http()
    {
        await using var server = await Harness.StartAsync();
        server.BaseAddress.Scheme.Should().Be("http");

        await using var client = await ConnectAsync(server.McpEndpoint);

        client.ServerInfo.Name.Should().Be("Windows-mcp");
        client.ServerInfo.Version.Should().Be(Program.ServerVersion,
            "the HTTP host must report the same identity the stdio host does");

        var tools = await client.ListToolsAsync();
        tools.Should().HaveCountGreaterThanOrEqualTo(60);
        FileWriteToolName(tools).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Api_key_gates_every_path_not_just_the_mcp_route()
    {
        await using var server = await Harness.StartAsync(apiKey: ApiKey);
        using var http = new HttpClient();

        foreach (var path in new[] { WindowsMcpHost.McpPath, "/", "/anything-else" })
        {
            using var response = await http.PostAsync(new Uri(server.BaseAddress, path), InitializeBody());

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"no credentials were sent to {path}");
            response.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, server.McpEndpoint) { Content = InitializeBody() })
        {
            request.Headers.Authorization = new("Bearer", ApiKey + "x");
            using var response = await http.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a wrong key must be refused");
        }

        // The right key, sent the way Claude Code sends it (a static header) -> full handshake.
        await using var client = await ConnectAsync(server.McpEndpoint, ApiKey);
        client.ServerInfo.Name.Should().Be("Windows-mcp");
    }

    [Fact]
    public async Task Certificate_makes_the_port_https_only()
    {
        using var cert = CreateEphemeralServerCertificate();
        await using var server = await Harness.StartAsync(cert: cert);
        server.BaseAddress.Scheme.Should().Be("https");

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var httpsClient = new HttpClient(handler);
        await using var client = await ConnectAsync(server.McpEndpoint, httpClient: httpsClient);

        client.ServerInfo.Name.Should().Be("Windows-mcp");
        (await client.ListToolsAsync()).Should().NotBeEmpty();

        // Plaintext on the same port: the TLS handshake fails and nothing is served in the clear.
        using var plain = new HttpClient();
        var plainUrl = new UriBuilder(server.McpEndpoint) { Scheme = "http" }.Uri;
        var act = () => plain.PostAsync(plainUrl, InitializeBody());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Caller_facing_refusals_reach_the_client_verbatim_over_http()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var fileWrite = FileWriteToolName(await client.ListToolsAsync());

        var scratch = Path.Combine(Path.GetTempPath(), $"windows-mcp-http-test-{Guid.NewGuid():N}.txt");
        var result = await client.CallToolAsync(fileWrite, new Dictionary<string, object?>
        {
            ["path"] = scratch,
            ["content"] = "must never be written",
            // no confirm:true -> the tool's ArgumentException, which the shared filter must surface as-is
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Be("'confirm: true' is required for file writes",
                "the ToolErrors filter registered in AddWindowsMcp applies to the HTTP transport too");
        File.Exists(scratch).Should().BeFalse();
    }

    // ---- A-7 ------------------------------------------------------------------------------

    /// <summary>
    /// A-7 risk #1 (A-roadmap section 6): <c>screenshot</c> changes its return type from
    /// <c>string</c> to <see cref="CallToolResult"/>. If <c>WithToolsFromAssembly</c> stopped
    /// discovering it, or the SDK rejected the return type, the tool would vanish from the list —
    /// this catches that without needing a desktop.
    /// </summary>
    [Fact]
    public async Task Screenshot_tool_is_still_discovered_with_a_CallToolResult_return_type()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var screenshot = tools.Single(t => t.Name.Replace("_", "").Equals("screenshot", StringComparison.OrdinalIgnoreCase));

        screenshot.Name.Should().Be(ScreenshotToolName(tools));
        var schema = screenshot.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var parameter in new[] { "region", "format", "output" })
            schema.TryGetProperty(parameter, out _).Should().BeTrue($"'{parameter}' is part of the A-7 signature");
        foreach (var parameter in new[] { "max_width", "max_height", "scale", "quality" })
            schema.TryGetProperty(parameter, out _).Should().BeTrue($"'{parameter}' is part of the A-9 signature");
    }

    /// <summary>
    /// A-9 (R1): the schema defaults and the description text are the only spec the model reads
    /// before it calls the tool, so they are a requirement in their own right — an advertised
    /// "default 1920" that the method does not actually apply is a lie the model acts on.
    /// <c>ScreenToolsTests.Screenshot_defaults_pass_the_1920x1080_cap_scale_1_and_quality_90</c>
    /// pins the other half (what the method does with the defaults).
    /// </summary>
    [Fact]
    public async Task Screenshot_schema_advertises_the_downscale_defaults_and_the_coordinate_scale_contract()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);

        var tools = await client.ListToolsAsync();
        var screenshot = tools.Single(t => t.Name == ScreenshotToolName(tools));
        var schema = screenshot.ProtocolTool.InputSchema.GetProperty("properties");

        schema.GetProperty("max_width").GetProperty("default").GetInt32().Should().Be(1920);
        schema.GetProperty("max_height").GetProperty("default").GetInt32().Should().Be(1080);
        schema.GetProperty("scale").GetProperty("default").GetDouble().Should().Be(1.0);
        schema.GetProperty("quality").GetProperty("default").GetInt32().Should().Be(90);

        var description = screenshot.ProtocolTool.Description;
        description.Should().Contain("1920x1080", "the prose default must match the schema default");
        description.Should().Contain("coordinateScale");
        description.Should().Contain("multiply image pixel coordinates",
            "the model is told once, in the tool description, how to undo the downscale");
    }

    /// <summary>
    /// The argument guards run before any capture, so this exercises the new tool over the real
    /// transport on a headless box: no screen is touched, but the call, the filter and the
    /// CallToolResult serialization all are.
    /// </summary>
    [Fact]
    public async Task Screenshot_argument_errors_reach_the_client_over_http()
    {
        await using var server = await Harness.StartAsync();
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["region"] = "not-a-region",   // rejected by ParseRegion before ScreenshotService is called
        });

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text
            .Should().Contain("Invalid region", "the ArgumentException is caller-facing and must survive the transport");
        result.Content.OfType<ImageContentBlock>().Should().BeEmpty();
    }

    // ---- A-9 ------------------------------------------------------------------------------

    /// <summary>
    /// A-9 (R9) end to end over the real transport, with <see cref="IScreenshotService"/> swapped
    /// for a mock through the new <c>BuildHttpApp</c> seam — so the downscale metadata contract is
    /// proven on a headless box, not only in the tool's own unit tests. Everything between the
    /// tool method and the JSON-RPC response (DI, the tool invoker, CallToolResult serialization)
    /// is real.
    /// </summary>
    [Fact]
    public async Task Screenshot_reports_the_original_size_and_coordinate_scale_over_http()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var screenshotService = new Mock<IScreenshotService>();
        screenshotService
            .Setup(s => s.CaptureAsync(It.IsAny<ScreenRegion?>(), It.IsAny<CaptureOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScreenshotResult(png, 2, 2, ImageFormat.Png, 4, 4, 2.0));

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(screenshotService.Object));
        await using var client = await ConnectAsync(server.McpEndpoint);
        var screenshot = ScreenshotToolName(await client.ListToolsAsync());

        // No arguments: the default agent-loop call.
        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>());

        result.IsError.Should().NotBe(true);

        var image = result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.DecodedData.ToArray().Should().Equal(png, "the mocked bytes must survive the round trip");
        image.MimeType.Should().Be("image/png", "the mime follows the ENCODED format, not the requested one");

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);
        meta.RootElement.GetProperty("width").GetInt32().Should().Be(2);
        meta.RootElement.GetProperty("height").GetInt32().Should().Be(2);
        meta.RootElement.GetProperty("originalWidth").GetInt32().Should().Be(4);
        meta.RootElement.GetProperty("originalHeight").GetInt32().Should().Be(4);
        meta.RootElement.GetProperty("coordinateScale").GetDouble().Should().Be(2.0);
        meta.RootElement.GetProperty("note").GetString().Should()
            .Be("multiply image pixel coordinates by 2 before passing them to click/drag/scroll");
    }

    /// <summary>
    /// The seam itself: a service registered through <c>configureServices</c> must be the one the
    /// tools resolve. Without this, the test above could pass for the wrong reason on a machine
    /// whose real screen happens to satisfy an assertion.
    /// </summary>
    [Fact]
    public async Task Configure_services_replaces_the_registration_AddWindowsMcp_made()
    {
        var screenshotService = new Mock<IScreenshotService>();

        await using var server = await Harness.StartAsync(
            configureServices: services => services.AddSingleton(screenshotService.Object));

        server.App.Services.GetRequiredService<IScreenshotService>()
            .Should().BeSameAs(screenshotService.Object, "configureServices runs AFTER AddWindowsMcp");
    }
}

/// <summary>
/// The one A-7 test that captures the real screen through the real HTTP host. Split out of
/// <see cref="HttpTransportTests"/> because that class is <c>Category=Integration</c> and a
/// vstest <c>Category!=UIAutomation</c> filter does not exclude a test that also carries
/// another Category value.
/// <para>
/// <c>Graphics.CopyFromScreen</c> needs an interactive desktop session (see the note on
/// <c>ScreenshotServiceTests</c>), so this fails headless — run it from an interactive session.
/// </para>
/// </summary>
[Trait("Category", "UIAutomation")]
public class HttpTransportScreenshotImageTests
{
    [Fact]
    public async Task Screenshot_returns_an_image_content_block_over_http()
    {
        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var screenshot = HttpTransportTests.ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["region"] = "0,0,64,48",   // small: this is a wiring proof, not a capture-quality test
            ["format"] = "png",
        });

        result.IsError.Should().NotBe(true);

        var image = result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.Data.Length.Should().BeGreaterThan(0, "the base64 image must survive the JSON-RPC round trip");
        image.MimeType.Should().Be("image/png");
        image.DecodedData.ToArray().Take(4).Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "PNG magic bytes");

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);
        meta.RootElement.GetProperty("width").GetInt32().Should().Be(64);
        meta.RootElement.GetProperty("height").GetInt32().Should().Be(48);
        meta.RootElement.GetProperty("coordinateSpace").GetString().Should().Be("virtual-desktop");
    }

    /// <summary>
    /// A-7 flipped the inline default to JPEG. Every mocked tool test hands back bytes a human
    /// wrote, so this is the only thing that proves the real Skia encode produces JPEG for the
    /// default path an agent loop actually takes (no <c>format</c> argument).
    /// </summary>
    [Fact]
    public async Task Screenshot_default_format_is_jpeg_over_http()
    {
        await using var server = await HttpTransportTests.Harness.StartAsync();
        await using var client = await HttpTransportTests.ConnectAsync(server.McpEndpoint);
        var screenshot = HttpTransportTests.ScreenshotToolName(await client.ListToolsAsync());

        var result = await client.CallToolAsync(screenshot, new Dictionary<string, object?>
        {
            ["region"] = "0,0,64,48",   // no 'format': exercise the default
        });

        result.IsError.Should().NotBe(true);

        var image = result.Content.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.MimeType.Should().Be("image/jpeg", "inline output defaults to jpeg (A-7)");
        image.DecodedData.ToArray().Take(3).Should().Equal(new byte[] { 0xFF, 0xD8, 0xFF }, "JPEG SOI marker");

        var text = result.Content.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        using var meta = JsonDocument.Parse(text.Text);
        meta.RootElement.GetProperty("format").GetString().Should().Be("jpeg");
        meta.RootElement.GetProperty("width").GetInt32().Should().Be(64);
    }
}

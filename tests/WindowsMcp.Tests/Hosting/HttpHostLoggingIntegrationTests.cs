using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using WindowsMcp.Hosting;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// fix/http-log-timestamps: the HTTP-mode timestamp has to survive the REAL host, not only the
/// helper. <see cref="WindowsMcpHost.BuildHttpApp"/> runs <c>WebApplication.CreateBuilder()</c>
/// first - which adds its own Console/Debug/EventSource/EventLog providers and binds the
/// <c>Logging</c> configuration section - and only then <c>ConfigureStderrLogging</c>. These tests
/// build (never start: nothing binds a port) that host and read what its container resolved. The
/// helper-level contract and the reasons an options-only assertion is not enough are in
/// <see cref="WindowsMcpHostTests"/> ("HTTP log timestamps"). Integration, like
/// <see cref="HttpTransportTests"/>: the real ASP.NET host with its environment and content root.
/// <para>
/// Ambient-environment dependency (review round 2, F2): CreateBuilder reads the process
/// environment (plain, <c>DOTNET_</c>- and <c>ASPNETCORE_</c>-prefixed variables) and any
/// appsettings*.json in the working directory, and under the "operator config wins where set"
/// policy a <c>Logging:Console</c> setting found there legitimately changes the stamp. The tests
/// that pin the built-in default therefore assert that precondition first, so a machine with such
/// a setting fails with that explanation rather than an obscure mismatch; nothing here mutates the
/// process environment. The configured cases themselves are covered hermetically in
/// <see cref="WindowsMcpHostTests"/>. The SDK-filter test similarly assumes no ambient
/// <c>Logging:LogLevel</c> overrides.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class HttpHostLoggingIntegrationTests
{
    private const string ApiKey = "integration-test-api-key-0123";
    private const int UnboundPort = 18765;   // never bound: these hosts are built, not started

    private static WebApplication BuildUnstartedHttpHost() =>
        WindowsMcpHost.BuildHttpApp(new ServerOptions(TransportKind.Http, "127.0.0.1", 0, null, null), cert: null);

    /// <summary>The precondition of every default-pinning test here; see the class remarks.</summary>
    private static void ShouldHaveNoAmbientConsoleLoggingConfig(WebApplication app)
    {
        var overrides = app.Configuration.GetSection("Logging:Console").AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value is not null)
            .Select(kv => $"Logging:Console:{kv.Key}={kv.Value}")
            .ToList();

        overrides.Should().BeEmpty(
            "this test pins the built-in HTTP default, and the ambient environment or appsettings*.json sets Logging:Console "
            + "values that the 'operator config wins' policy honours by design - unset them to run it ({0})",
            string.Join("; ", overrides));
    }

    [Fact]
    public async Task BuildHttpApp_resolves_the_local_millisecond_timestamp_format()
    {
        await using var app = BuildUnstartedHttpHost();
        ShouldHaveNoAmbientConsoleLoggingConfig(app);

        var simple = app.Services.GetRequiredService<IOptionsMonitor<SimpleConsoleFormatterOptions>>().CurrentValue;

        simple.TimestampFormat.Should().Be(WindowsMcpHostTests.HttpTimestampFormat,
            "nothing CreateBuilder adds, and nothing registered after ConfigureStderrLogging, may drop the HTTP-mode stamp");
        simple.UseUtcTimestamp.Should().BeFalse("the stamp is local time; zzz carries the offset");
    }

    [Fact]
    public async Task BuildHttpApp_renders_log_lines_with_a_leading_local_timestamp()
    {
        await using var app = BuildUnstartedHttpHost();
        ShouldHaveNoAmbientConsoleLoggingConfig(app);

        var before = DateTimeOffset.Now;
        var line = WindowsMcpHostTests.RenderFirstLine(app.Services);
        var after = DateTimeOffset.Now;

        WindowsMcpHostTests.ShouldStartWithALocalTimestampForNow(line, before, after);
    }

    [Fact]
    public async Task BuildHttpApp_keeps_a_single_console_provider_on_stderr_and_quiets_the_mcp_sdk()
    {
        await using var app = BuildUnstartedHttpHost();

        app.Services.GetServices<ILoggerProvider>().Should().ContainSingle(
                "ClearProviders drops the EventLog, Debug and EventSource providers CreateBuilder adds")
            .Which.Should().BeOfType<ConsoleLoggerProvider>();
        app.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.LogToStandardErrorThreshold
            .Should().Be(LogLevel.Trace, "every level goes to stderr in HTTP mode too");

        var factory = app.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger(WindowsMcpHostTests.SdkCategory).IsEnabled(LogLevel.Information).Should().BeFalse(
            "stateless HTTP builds a server per request; the SDK is filtered to Warning");
        factory.CreateLogger(WindowsMcpHostTests.SdkCategory).IsEnabled(LogLevel.Warning).Should().BeTrue();
        factory.CreateLogger(WindowsMcpHostTests.OwnCategory).IsEnabled(LogLevel.Information).Should().BeTrue(
            "the server's own Information lines, e.g. the 'listening at' banner, still print");
    }

    // ---- F3: the plain-HTTP warning goes through the logger --------------------------------
    // Review round 2: Program.RunHttpAsync wrote "Windows-mcp: WARNING: listening on <bind>:<port>
    // over plain HTTP ..." straight to Console.Error, so the one line an off-loopback operator most
    // needs to date carried no stamp. BuildHttpApp now logs it as a Warning once the host is built.
    // A recording provider is added through configureServices, which runs after
    // ConfigureStderrLogging's ClearProviders, so it survives and sees what the console sees.

    /// <summary>Keeps every entry any logger of the host writes.</summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(string Category, LogLevel Level, string Message)> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, _entries);

        public void Dispose() { }

        private sealed class Recorder(string category, ConcurrentQueue<(string, LogLevel, string)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                sink.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    private static List<(string Category, LogLevel Level, string Message)> PlainHttpWarnings(RecordingLoggerProvider recorder) =>
        recorder.Entries
            .Where(e => e.Message.Contains("plain HTTP", StringComparison.OrdinalIgnoreCase)
                     || e.Message.Contains("unencrypted", StringComparison.OrdinalIgnoreCase))
            .ToList();

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public async Task BuildHttpApp_off_loopback_without_a_certificate_logs_one_plain_HTTP_warning(string bind)
    {
        var recorder = new RecordingLoggerProvider();
        await using var app = WindowsMcpHost.BuildHttpApp(
            new ServerOptions(TransportKind.Http, bind, UnboundPort, null, ApiKey), cert: null,
            services => services.AddSingleton<ILoggerProvider>(recorder));

        var warning = PlainHttpWarnings(recorder).Should().ContainSingle(
            "BuildHttpApp itself logs the plain-HTTP warning exactly once, so it carries the HTTP-mode stamp").Subject;

        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("plain HTTP")
            .And.Contain($"{bind}:{UnboundPort}", "the operator needs to see which listener is exposed")
            .And.Contain("unencrypted")
            .And.Contain("--cert-thumbprint", "the warning names the fix");
        warning.Message.Should().NotContain("Windows-mcp: WARNING",
            "the log level and category already say that; the old raw-stderr prefix would repeat it");
        warning.Message.Should().NotContain(ApiKey, "a warning about exposure must not itself print the API key");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task BuildHttpApp_on_loopback_without_a_certificate_logs_no_plain_HTTP_warning(string bind)
    {
        var recorder = new RecordingLoggerProvider();
        await using var app = WindowsMcpHost.BuildHttpApp(
            new ServerOptions(TransportKind.Http, bind, UnboundPort, null, null), cert: null,
            services => services.AddSingleton<ILoggerProvider>(recorder));

        PlainHttpWarnings(recorder).Should().BeEmpty("traffic that never leaves the machine is not 'crossing the network unencrypted'");
    }

    [Fact]
    public async Task BuildHttpApp_off_loopback_with_a_certificate_logs_no_plain_HTTP_warning()
    {
        using var cert = HttpTransportTests.CreateEphemeralServerCertificate();
        var recorder = new RecordingLoggerProvider();
        await using var app = WindowsMcpHost.BuildHttpApp(
            new ServerOptions(TransportKind.Http, "0.0.0.0", UnboundPort, cert.Thumbprint, ApiKey), cert,
            services => services.AddSingleton<ILoggerProvider>(recorder));

        PlainHttpWarnings(recorder).Should().BeEmpty("with a certificate the port serves HTTPS only");
    }
}

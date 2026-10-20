using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using WindowsMcp.Hosting;

namespace WindowsMcp.Tests.Hosting;

/// <summary>
/// fix/http-log-timestamps, end to end: the server exe the build copies beside the tests is
/// started for real (<c>Program.Main</c> into <c>RunHttpAsync</c> / <c>RunStdioAsync</c>) and its
/// own stderr is read back. The in-process tests (<see cref="WindowsMcpHostTests"/>, "HTTP log
/// timestamps", and <see cref="HttpHostLoggingIntegrationTests"/>) render through the formatter
/// the console provider would select; these read what the provider actually wrote, through the
/// real Program wiring - including the stdio host, which no other test builds. Self-cleaning: a
/// loopback-only listener on a free port behind a random API key (or a stdio server with its
/// stdin held open), started in an empty working directory with the logging-relevant environment
/// scrubbed, and killed as soon as its startup lines have been read. No request is ever sent.
/// The off-loopback cases bind TEST-NET-1, an address this machine never owns: the host is built
/// and logs, then fails to bind and exits, without ever listening on a real interface.
/// </summary>
[Trait("Category", "Integration")]
public class ServerProcessLoggingIntegrationTests
{
    /// <summary>Generous: the first launch of a fresh build can be held up by Defender scanning.</summary>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(90);

    /// <summary>An unstamped SimpleConsole entry: its first line starts at the level token.</summary>
    private static readonly Regex LevelToken = new(@"^(trce|dbug|info|warn|fail|crit): ", RegexOptions.CultureInvariant);

    private static readonly Regex AnsiColor = new(@"\x1B\[[0-9;]*m", RegexOptions.CultureInvariant);

    /// <summary>TEST-NET-1 (RFC 5737): documentation-only, never assigned to this machine.</summary>
    private const string UnownedAddress = "192.0.2.1";
    private const int UnownedPort = 18766;

    /// <summary>
    /// Configuration keys (environment names with <c>__</c> read as <c>:</c>) that would reach the
    /// child's <c>Logging</c> section: plain variables are app configuration, and the
    /// <c>DOTNET_</c>/<c>ASPNETCORE_</c>-prefixed ones are host configuration folded into it.
    /// </summary>
    private static readonly string[] ScrubbedConfigPrefixes = ["Logging:", "DOTNET_Logging:", "ASPNETCORE_Logging:"];

    /// <summary>
    /// Other variables that change which host starts or how its console logger writes.
    /// DOTNET_/ASPNETCORE_ENVIRONMENT are deliberately NOT scrubbed: with an empty content root
    /// there is no appsettings.{env}.json to pick, and the server has no UserSecretsId, so the
    /// environment name cannot reach logging - while removing a registry-defined one only makes
    /// EnvironmentRepair restore it and print its repair notice.
    /// </summary>
    private static readonly string[] ScrubbedNames =
    [
        "DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION",   // forces colour codes into redirected output
        "DOTNET_CONTENTROOT", "ASPNETCORE_CONTENTROOT",         // move the content root (and its appsettings*.json) off the empty directory
        "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES",                  // a hosting startup can reconfigure logging
        "DOTNET_STARTUP_HOOKS",                                 // arbitrary code before Main
    ];

    private static bool IsScrubbed(string name) =>
        name.StartsWith(ServerOptions.EnvPrefix, StringComparison.OrdinalIgnoreCase)   // transport, bind, cert, key
        || ScrubbedConfigPrefixes.Any(p => name.Replace("__", ":", StringComparison.Ordinal).StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || ScrubbedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A scrubbed name whose registry value would change these results once restored.</summary>
    private static bool MattersIfRestored(string name)
    {
        // WINDOWSMCP_*: transport/bind/port are passed as flags (which win), API_KEY is set
        // explicitly for HTTP and ignored by stdio; a certificate would turn the listener into HTTPS.
        if (name.StartsWith(ServerOptions.EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return name.Equals(ServerOptions.EnvPrefix + "CERT_THUMBPRINT", StringComparison.OrdinalIgnoreCase);
        // Forced colour codes are stripped before any assertion.
        if (name.Equals("DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION", StringComparison.OrdinalIgnoreCase))
            return false;
        return IsScrubbed(name);
    }

    /// <summary>
    /// Program.Main's first step, EnvironmentRepair, refills every variable the user or machine
    /// environment defines but the process lacks - so removing such a variable from the child's
    /// block does not keep it out; the child reads it back from the registry. Those have to be
    /// unset at the source for these tests to be hermetic. Names only are reported, never values.
    /// </summary>
    private static void ShouldHaveNoScrubbedVariableInTheRegistry()
    {
        var restorable = new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(t => Environment.GetEnvironmentVariables(t).Keys.Cast<string>().Select(name => $"{t}:{name}"))
            .Where(entry => MattersIfRestored(entry[(entry.IndexOf(':') + 1)..]))
            .ToList();

        restorable.Should().BeEmpty(
            "EnvironmentRepair restores registry-defined variables the child lacks, so scrubbing cannot keep these out - "
            + "unset them in the user/machine environment to run this test ({0})", string.Join(", ", restorable));
    }

    /// <param name="ExitCode">Set when the server exited on its own; null when the test killed it.</param>
    private sealed record ServerRun(IReadOnlyList<string> Stderr, string Stdout, DateTimeOffset Started, DateTimeOffset Ready, int? ExitCode);

    /// <summary>
    /// Starts the server, collects its output until a line on either stream contains
    /// <paramref name="readyText"/>, then kills it. Either stream: a log line misrouted to stdout
    /// must fail the stdout assertion at once, not wait out the startup budget.
    /// </summary>
    private static Task<ServerRun> RunUntilAsync(string arguments, string readyText, string? apiKey = null) =>
        RunAsync(arguments, readyText, apiKey, extraEnvironment: null);

    /// <summary>Starts the server and collects everything it writes until it exits on its own.</summary>
    private static Task<ServerRun> RunToExitAsync(
        string arguments, string apiKey, IReadOnlyDictionary<string, string>? extraEnvironment = null) =>
        RunAsync(arguments, readyText: null, apiKey, extraEnvironment);

    /// <summary>
    /// <paramref name="apiKey"/>, when given, is handed over as <c>WINDOWSMCP_API_KEY</c> so an
    /// HTTP child that outlives a crashed test run is never open; <paramref name="extraEnvironment"/>
    /// is applied after the scrub, so it is the only logging configuration the child sees.
    /// </summary>
    private static async Task<ServerRun> RunAsync(
        string arguments, string? readyText, string? apiKey, IReadOnlyDictionary<string, string>? extraEnvironment)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "WindowsMcp.exe");
        File.Exists(exe).Should().BeTrue("the test project references the server, so its apphost is copied beside the tests");
        ShouldHaveNoScrubbedVariableInTheRegistry();

        // Empty working directory = empty content root: no appsettings*.json can configure logging.
        var workDir = Path.Combine(Path.GetTempPath(), "wmcp-logging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,   // held open: EOF on stdin ends a stdio server
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir,
        };
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (IsScrubbed(key))
                psi.Environment.Remove(key);
        }
        if (apiKey is not null)
            psi.Environment[ServerOptions.EnvPrefix + "API_KEY"] = apiKey;
        foreach (var (name, value) in extraEnvironment ?? new Dictionary<string, string>())
            psi.Environment[name] = value;

        var stderr = new ConcurrentQueue<string>();
        var stdout = new StringBuilder();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string StdoutSoFar() { lock (stdout) return stdout.ToString(); }

        using var process = new Process { StartInfo = psi };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.Enqueue(e.Data);
            if (readyText is not null && e.Data.Contains(readyText, StringComparison.Ordinal)) ready.TrySetResult();
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            if (readyText is not null && e.Data.Contains(readyText, StringComparison.Ordinal)) ready.TrySetResult();
        };

        var launched = false;
        var exitedOnItsOwn = false;
        DateTimeOffset started = default, readyAt = default;
        try
        {
            started = DateTimeOffset.Now;
            launched = process.Start();
            launched.Should().BeTrue("a fresh, non-shell process start always launches");
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            if (readyText is null)
            {
                _ = await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(StartupBudget));
                readyAt = DateTimeOffset.Now;
                exitedOnItsOwn = process.HasExited;
                exitedOnItsOwn.Should().BeTrue(
                    "the server must exit on its own within {0}. stderr so far:\n{1}", StartupBudget, string.Join("\n", stderr));
            }
            else
            {
                _ = await Task.WhenAny(ready.Task, process.WaitForExitAsync(), Task.Delay(StartupBudget));
                readyAt = DateTimeOffset.Now;
                ready.Task.IsCompleted.Should().BeTrue(
                    "the server must log \"{0}\" within {1}; it {2}. stderr so far:\n{3}\nstdout so far:\n{4}",
                    readyText, StartupBudget, process.HasExited ? $"exited with code {process.ExitCode}" : "is still starting",
                    string.Join("\n", stderr), StdoutSoFar());
            }
        }
        finally
        {
            if (launched)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                await process.WaitForExitAsync();   // also drains both redirected streams to EOF
            }
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }

        return new ServerRun(stderr.ToArray(), StdoutSoFar(), started, readyAt, exitedOnItsOwn ? process.ExitCode : null);
    }

    /// <summary>
    /// The environment-repair notice Program writes before any host, in both transports, when the
    /// runner's environment lacks PATHEXT, ProgramData, a System32 Path entry or any other
    /// registry-defined variable. It is the one raw <c>Windows-mcp:</c> line a healthy loopback
    /// start can legitimately print (a stamp on it would double in stdio), so it is the only one allowed.
    /// </summary>
    private static bool IsRepairNotice(string line) =>
        line.StartsWith("Windows-mcp: repaired environment (", StringComparison.Ordinal);

    /// <summary>stderr with ANSI colour codes removed (they are forced on by some environments).</summary>
    private static List<string> Plain(IEnumerable<string> stderr) =>
        stderr.Select(l => AnsiColor.Replace(l, string.Empty)).ToList();

    /// <summary>Every column-0 line except the repair notice: a SimpleConsole entry's message lines are indented.</summary>
    private static List<string> LogEntryHeaders(IEnumerable<string> plainStderr) =>
        plainStderr.Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && !IsRepairNotice(l)).ToList();

    private static void ShouldHaveNoRawProgramLines(IEnumerable<string> plainStderr) =>
        plainStderr.Should().NotContain(l => l.StartsWith("Windows-mcp:", StringComparison.Ordinal) && !IsRepairNotice(l),
            "a healthy loopback start writes everything through the logger; a raw 'Windows-mcp:' line bypasses the stamp");

    /// <summary>
    /// The HTTP stamp as the child renders it. The child formats with the user's culture, which it
    /// shares with this test process, and ':' in a .NET custom time format is that culture's time
    /// separator (the plain format is deliberate); the ':' inside zzz is literal.
    /// </summary>
    private static Regex StampedHeader()
    {
        var sep = Regex.Escape(CultureInfo.CurrentCulture.DateTimeFormat.TimeSeparator);
        return new Regex(
            $@"^(?<ts>\d{{4}}-\d{{2}}-\d{{2}} \d{{2}}{sep}\d{{2}}{sep}\d{{2}}\.\d{{3}} [+-]\d{{2}}:\d{{2}}) (trce|dbug|info|warn|fail|crit): ",
            RegexOptions.CultureInvariant);
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string NewApiKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));   // 48 chars

    private static List<string> PlainHttpLines(IEnumerable<string> plainStderr) =>
        plainStderr.Where(l => l.Contains("plain HTTP", StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task Http_server_process_stamps_every_stderr_log_entry_with_local_time()
    {
        var apiKey = NewApiKey();
        var run = await RunUntilAsync(
            $"--transport http --bind 127.0.0.1 --port {FreeLoopbackPort()}", "listening at", apiKey);

        run.Stdout.Should().BeEmpty("HTTP mode keeps stdio's sink: every log level goes to stderr");

        var stderr = Plain(run.Stderr);
        stderr.Should().Contain(l => l.Contains("auth: bearer", StringComparison.Ordinal),
            "the child was given an API key, so even an orphaned listener is not open");
        stderr.Should().NotContain(l => l.Contains(apiKey, StringComparison.Ordinal), "the API key is never logged");
        ShouldHaveNoRawProgramLines(stderr);

        var headers = LogEntryHeaders(stderr);
        headers.Should().HaveCountGreaterThanOrEqualTo(2,
            "the host's 'Now listening on' entry and the server's own 'listening at' banner are both logged by then");

        var stamped = StampedHeader();
        foreach (var header in headers)
        {
            var match = stamped.Match(header);
            match.Success.Should().BeTrue(
                "every HTTP-mode log entry on stderr must start '<yyyy-MM-dd HH:mm:ss.fff zzz> <level>: ', but the server wrote \"{0}\"",
                header);

            var stamp = DateTimeOffset.ParseExact(
                match.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.CurrentCulture);
            stamp.Should().BeOnOrAfter(run.Started.AddSeconds(-2)).And.BeOnOrBefore(run.Ready.AddSeconds(2),
                "the stamp is the wall-clock moment the entry was written, on a 24-hour clock");
            stamp.Offset.Should().Be(TimeZoneInfo.Local.GetUtcOffset(stamp.UtcDateTime),
                "the stamp is local time carrying its UTC offset, not UTC");
        }
    }

    [Fact]
    public async Task Stdio_server_process_leaves_stderr_log_entries_unstamped()
    {
        var run = await RunUntilAsync("--transport stdio", "Application started");

        run.Stdout.Should().BeEmpty("stdout is the JSON-RPC channel and no request was sent, so no log line may reach it");

        var stderr = Plain(run.Stderr);
        ShouldHaveNoRawProgramLines(stderr);

        var headers = LogEntryHeaders(stderr);
        headers.Should().NotBeEmpty("the host's 'Application started' entry has been logged by then");
        headers.Should().OnlyContain(h => LevelToken.IsMatch(h),
            "the MCP host prefixes its own timestamp to every stderr line it captures, so a stdio entry starts at the level token");
    }

    // ---- R2-1: the plain-HTTP warning must survive logging configuration ---------------------
    // Round 2 moved the warning from an unconditional raw stderr write into app.Logger.LogWarning,
    // so a Logging configuration that hides Warning for the app's category silently dropped it.
    // At default levels it is one stamped, logged entry; when the logger would drop it, it is one
    // raw line instead - never neither, never both.

    [Fact]
    public async Task Http_server_process_off_loopback_without_a_certificate_logs_one_stamped_warning()
    {
        var apiKey = NewApiKey();
        var run = await RunToExitAsync($"--transport http --bind {UnownedAddress} --port {UnownedPort}", apiKey);

        run.ExitCode.Should().NotBe(0, "the host is built and warns, then cannot bind an address this machine does not own");

        var stderr = Plain(run.Stderr);
        PlainHttpLines(stderr).Should().ContainSingle("the plain-HTTP warning is written exactly once")
            .Which.Should().Contain($"{UnownedAddress}:{UnownedPort}")
            .And.Contain("unencrypted")
            .And.Contain("--cert-thumbprint");
        stderr.Should().NotContain(l => l.StartsWith("Windows-mcp: WARNING", StringComparison.Ordinal),
            "at default levels the logger carries the warning; a raw copy beside it would be an unstamped duplicate");

        var index = stderr.FindIndex(l => l.Contains("plain HTTP", StringComparison.Ordinal));
        index.Should().BeGreaterThan(0, "a SimpleConsole entry's message line follows its header line");
        var header = stderr[index - 1];
        StampedHeader().IsMatch(header).Should().BeTrue(
            "the warning's entry starts with the HTTP-mode stamp, but its header line was \"{0}\"", header);
        header.Should().Contain(" warn: ", "the entry is logged at Warning");
        stderr.Should().NotContain(l => l.Contains(apiKey, StringComparison.Ordinal), "the API key is never written");
    }

    [Theory]
    [InlineData("Logging__LogLevel__Default", "Error")]
    [InlineData("Logging__Console__LogLevel__Default", "Error")]
    [InlineData("Logging__LogLevel__WindowsMcp", "None")]   // app.Logger's category is the application name
    public async Task Http_server_process_off_loopback_without_a_certificate_still_warns_when_config_hides_warnings(
        string variable, string value)
    {
        var apiKey = NewApiKey();
        var run = await RunToExitAsync(
            $"--transport http --bind {UnownedAddress} --port {UnownedPort}", apiKey,
            new Dictionary<string, string> { [variable] = value });

        run.ExitCode.Should().NotBe(0, "the host is built and warns, then cannot bind an address this machine does not own");

        var stderr = Plain(run.Stderr);
        PlainHttpLines(stderr).Should().ContainSingle(
                "{0}={1} filters the logged warning out, and an operator exposing every tool unencrypted must still be told - once",
                variable, value)
            .Which.Should().StartWith($"Windows-mcp: WARNING: listening on {UnownedAddress}:{UnownedPort} over plain HTTP",
                "with the logger unable to carry it, the warning falls back to the raw stderr line")
            .And.Contain("unencrypted")
            .And.Contain("--cert-thumbprint");
        stderr.Should().NotContain(l => l.Contains(apiKey, StringComparison.Ordinal), "the API key is never written");
    }
}

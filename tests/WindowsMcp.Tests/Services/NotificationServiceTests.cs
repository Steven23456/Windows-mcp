using System.Runtime.InteropServices;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// C-4: the toast goes in-process through <see cref="IToastSink"/> (WinRT's
/// <c>CreateToastNotifier(appId).Show(xml)</c>) instead of a PowerShell cold start, and the
/// server registers its own AppUserModelId under HKCU so the platform stops dropping the toast
/// with <c>0x80070490</c>. The sink and the clock are seams; the registry is
/// <see cref="IRegistryService"/>, so the unit tests see the exact key, value and kind.
/// </summary>
[Trait("Category", "Unit")]
public class NotificationServiceTests
{
    private const string DefaultAppId = "Windows-MCP";
    private const string RegistrationKey = @"Software\Classes\AppUserModelId\Windows-MCP";

    /// <summary>The element-not-found HResult the spike saw for an id the platform does not know.</summary>
    private static readonly int ElementNotFound = unchecked((int)0x80070490);

    private sealed class FakeSink : IToastSink
    {
        /// <summary>Every attempt, in order, whether it threw or not.</summary>
        public List<(string AppId, string Xml)> Attempts { get; } = [];

        /// <summary>Interleaved "show"/"delay" so the retry's ordering can be asserted.</summary>
        public List<string> Log { get; } = [];

        /// <summary>Thrown, in order, one per Show call; an empty queue means success.</summary>
        public Queue<Exception> Failures { get; } = new();

        public void Show(string appId, string toastXml)
        {
            Log.Add("show");
            Attempts.Add((appId, toastXml));
            if (Failures.Count > 0) throw Failures.Dequeue();
        }
    }

    /// <summary>A registry where no AppUserModelId key exists: every read is a miss.</summary>
    private static Mock<IRegistryService> NoRegistrations()
    {
        var mock = new Mock<IRegistryService>();
        mock.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Registry path not found: HKCU\\..."));
        return mock;
    }

    private static void VerifyRegistrationWritten(Mock<IRegistryService> registry, Times times) =>
        registry.Verify(s => s.SetAsync("HKCU", RegistrationKey, "DisplayName", DefaultAppId, "String",
            It.IsAny<CancellationToken>()), times);

    private static void VerifyNothingWritten(Mock<IRegistryService> registry) =>
        registry.Verify(s => s.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<object>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    // ---- R1: registration ---------------------------------------------------------------------

    /// <summary>
    /// C-4 (R1): the documented registration for an unpackaged exe - DisplayName under
    /// HKCU\Software\Classes\AppUserModelId\Windows-MCP - written once, not once per toast.
    /// </summary>
    [Fact]
    public async Task The_default_id_registers_DisplayName_once_across_two_calls()
    {
        var registry = NoRegistrations();
        var sink = new FakeSink();
        var service = new NotificationService(registry.Object, sink);

        await service.ShowAsync("one", "first");
        await service.ShowAsync("two", "second");

        VerifyRegistrationWritten(registry, Times.Once());
        sink.Attempts.Should().HaveCount(2);
    }

    /// <summary>
    /// C-4 (R1): the once-per-process guard lives on the instance - the host registers the service
    /// as a singleton, which is what makes it once per process. A static flag would leak across
    /// callers (and across these tests) and could never be exercised twice.
    /// </summary>
    [Fact]
    public async Task A_second_service_instance_registers_again()
    {
        var registry = NoRegistrations();

        await new NotificationService(registry.Object, new FakeSink()).ShowAsync("one", "first");
        await new NotificationService(registry.Object, new FakeSink()).ShowAsync("two", "second");

        VerifyRegistrationWritten(registry, Times.Exactly(2));
    }

    /// <summary>C-4 (R1): an id the platform already knows needs no write.</summary>
    [Fact]
    public async Task The_default_id_is_not_rewritten_when_the_key_is_already_there()
    {
        var registry = new Mock<IRegistryService>();
        registry.Setup(s => s.ListAsync("HKCU", RegistrationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryKeyDto(RegistrationKey,
                [new RegistryValueDto(RegistrationKey, "DisplayName", DefaultAppId, "String")], []));

        var result = await new NotificationService(registry.Object, new FakeSink()).ShowAsync("t", "m");

        VerifyNothingWritten(registry);
        result.Registered.Should().BeTrue();
    }

    /// <summary>
    /// C-4 (R1): a caller's id is used, never written. Writing it would be a registry change
    /// behind a tool with no confirm gate.
    /// </summary>
    [Fact]
    public async Task A_custom_id_is_never_written_to_the_registry()
    {
        var registry = NoRegistrations();
        var sink = new FakeSink();

        var result = await new NotificationService(registry.Object, sink).ShowAsync("t", "m", "Contoso.App");

        VerifyNothingWritten(registry);
        result.AppId.Should().Be("Contoso.App");
        sink.Attempts.Single().AppId.Should().Be("Contoso.App");
        result.Registered.Should().BeFalse("no AppUserModelId key exists for it under either hive");
    }

    /// <summary>C-4 (R1): a packaged AUMID carries a '!' and is registered by construction.</summary>
    [Fact]
    public async Task A_packaged_id_reports_registered_without_a_key()
    {
        var registry = NoRegistrations();

        var result = await new NotificationService(registry.Object, new FakeSink())
            .ShowAsync("t", "m", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App");

        result.Registered.Should().BeTrue("a packaged id is the package's own identity");
        result.Shown.Should().BeTrue();
    }

    /// <summary>C-4 (R1): a machine-wide registration counts too.</summary>
    [Fact]
    public async Task An_id_registered_under_HKLM_reports_registered()
    {
        var registry = new Mock<IRegistryService>();
        registry.Setup(s => s.ListAsync("HKCU", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("not here"));
        registry.Setup(s => s.ListAsync("HKLM",
                It.Is<string>(p => p.Contains("AppUserModelId", StringComparison.OrdinalIgnoreCase)
                                   && p.EndsWith("Contoso.App", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryKeyDto("Contoso.App", [], []));

        var result = await new NotificationService(registry.Object, new FakeSink())
            .ShowAsync("t", "m", "Contoso.App");

        result.Registered.Should().BeTrue();
        VerifyNothingWritten(registry);
    }

    /// <summary>C-4 (R1): a blank id is a caller error, not an id.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_app_id_is_refused(string appId)
    {
        var sink = new FakeSink();

        Func<Task> act = () => new NotificationService(NoRegistrations().Object, sink).ShowAsync("t", "m", appId);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*app*");
        sink.Attempts.Should().BeEmpty();
    }

    // ---- R2: the payload, the retry and the dependency ----------------------------------------

    /// <summary>
    /// C-4 (R2): the payload is a ToastGeneric binding with the title and the body, XML-escaped -
    /// an unescaped ampersand in a title makes LoadXml throw and the toast vanish.
    /// </summary>
    [Fact]
    public async Task The_sink_receives_the_escaped_title_and_message_in_a_toast_generic_payload()
    {
        var sink = new FakeSink();

        await new NotificationService(NoRegistrations().Object, sink)
            .ShowAsync("Tom & Jerry", "<b>done</b> \"ok\"");

        var (appId, xml) = sink.Attempts.Should().ContainSingle().Subject;
        appId.Should().Be(DefaultAppId);
        xml.Should().Contain("ToastGeneric");
        xml.Should().Contain("Tom &amp; Jerry");
        xml.Should().Contain("&lt;b&gt;done&lt;/b&gt;");
        xml.Should().Contain("&quot;ok&quot;");
        xml.Should().NotContain("<b>done</b>", "an unescaped tag would be parsed as toast markup");
    }

    /// <summary>
    /// C-4 (R2): the spike's finding - the first show right after registration can fail before the
    /// platform has picked the key up. One wait, one retry, and the toast lands.
    /// </summary>
    [Fact]
    public async Task A_transient_element_not_found_is_retried_once_after_the_delay()
    {
        var sink = new FakeSink();
        sink.Failures.Enqueue(new COMException("Element not found", ElementNotFound));
        var delays = new List<TimeSpan>();
        Task Delay(TimeSpan d, CancellationToken _) { delays.Add(d); sink.Log.Add("delay"); return Task.CompletedTask; }

        var result = await new NotificationService(NoRegistrations().Object, sink, Delay).ShowAsync("t", "m");

        result.Shown.Should().BeTrue();
        result.Note.Should().BeNull("the toast was shown in the end; there is nothing to explain");
        sink.Log.Should().Equal(new[] { "show", "delay", "show" },
            "the retry waits for the platform to pick the registration up before trying again");
        delays.Should().ContainSingle().Which.Should().BeGreaterThan(TimeSpan.Zero);
    }

    /// <summary>
    /// C-4 (R2): when it keeps failing the call is not an exception - the model gets a result that
    /// names the id and the HResult so it can register the id or pass a packaged one.
    /// </summary>
    [Fact]
    public async Task A_persistent_element_not_found_reports_shown_false_with_a_note()
    {
        var sink = new FakeSink();
        sink.Failures.Enqueue(new COMException("Element not found", ElementNotFound));
        sink.Failures.Enqueue(new COMException("Element not found", ElementNotFound));

        var result = await new NotificationService(NoRegistrations().Object, sink,
            (_, _) => Task.CompletedTask).ShowAsync("t", "m", "Contoso.App");

        result.Shown.Should().BeFalse();
        result.AppId.Should().Be("Contoso.App");
        result.Note.Should().NotBeNull();
        result.Note!.Should().Contain("Contoso.App").And.ContainEquivalentOf("0x80070490");
        sink.Attempts.Should().HaveCount(2, "one retry, not a loop");
    }

    /// <summary>C-4 (R2): only 0x80070490 is the known-transient one; anything else is a bug.</summary>
    [Fact]
    public async Task Another_COM_failure_propagates()
    {
        var sink = new FakeSink();
        sink.Failures.Enqueue(new COMException("Catastrophic failure", unchecked((int)0x8000FFFF)));

        Func<Task> act = () => new NotificationService(NoRegistrations().Object, sink,
            (_, _) => Task.CompletedTask).ShowAsync("t", "m");

        await act.Should().ThrowAsync<COMException>();
        sink.Attempts.Should().ContainSingle("an unknown failure is not retried");
    }

    /// <summary>
    /// C-4 (R2): the PowerShell route is gone. A constructor that still took
    /// <see cref="IPowerShellService"/> would mean a toast still pays a cold start and takes the
    /// serialization gate.
    /// </summary>
    [Fact]
    public void The_service_no_longer_depends_on_powershell()
    {
        var parameters = typeof(NotificationService).GetConstructors(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        parameters.Should().NotContain(typeof(IPowerShellService));
        typeof(NotificationService).GetConstructors().Should().ContainSingle(
                "the DI container resolves the public constructor")
            .Which.GetParameters().Select(p => p.ParameterType)
            .Should().Equal(new[] { typeof(IRegistryService) });
    }

    /// <summary>
    /// C-4: a cancelled call shows nothing and writes nothing. The registration is a registry
    /// write, so the cancellation check has to come before it, not between it and the show.
    /// </summary>
    [Fact]
    public async Task A_cancelled_token_stops_the_toast_before_the_registry_or_the_sink_is_touched()
    {
        var registry = NoRegistrations();
        var sink = new FakeSink();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => new NotificationService(registry.Object, sink)
            .ShowAsync("t", "m", DefaultAppId, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.Attempts.Should().BeEmpty();
        VerifyNothingWritten(registry);
    }
}

/// <summary>
/// The one C-4 test that goes all the way to Windows. It puts a real toast on the desktop (and
/// in the Action Center) under the server's own AppUserModelId, through the real registry
/// service — the mocked tests above would all stay green if <c>WinRtToastSink</c> silently threw,
/// which is exactly the failure mode the D-era disk_inspect bug had.
/// </summary>
[Trait("Category", "Integration")]
public class NotificationServiceIntegrationTests : IDisposable
{
    private const string RegistrationKey = @"Software\Classes\AppUserModelId\Windows-MCP";

    private static Microsoft.Win32.RegistryKey? OpenRegistration() =>
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistrationKey);

    /// <summary>Was the server's own AUMID already registered before this test ran?</summary>
    private readonly bool _wasRegistered = Registered();

    private static bool Registered()
    {
        using var key = OpenRegistration();
        return key is not null;
    }

    public void Dispose()
    {
        // Restore what the test touched: if this box had no registration before, take ours back
        // out. (The platform remembers the id anyway - the C-4 spike recorded that.)
        if (!_wasRegistered)
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RegistrationKey); } catch { }
        }
    }

    /// <summary>
    /// Two toasts appear on the desktop when this runs; that is the point of the test. The second
    /// call is what pins <c>registered</c> deterministically: by then the key is there whether or
    /// not this box had it before.
    /// <para>
    /// C-4 (R4) asked for "no <c>powershell.exe</c> spawned". A by-name process scan cannot say
    /// that here: xUnit runs this class in parallel with <c>PowerShellServiceTests</c> and
    /// <c>JobServiceTests</c>, which spawn their own <c>powershell.exe</c> in the same host
    /// process, so the scan fails on somebody else's child (observed: pid 20796). Neither does a
    /// children-of-this-process scan - those spawns are children of this same test host. The
    /// sound evidence is the elapsed time: a PowerShell route pays a cold start per toast (15-75 s
    /// each on this box, per CLAUDE.md) plus the service's serialization gate, while the WinRT
    /// route is milliseconds plus at most one 1 s registration retry per call. Two shows inside
    /// ten seconds cannot have been two PowerShell cold starts. The constructor shape is pinned
    /// separately by <c>The_service_no_longer_depends_on_powershell</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_real_toast_is_shown_under_the_default_id_without_spawning_powershell()
    {
        var service = new NotificationService(new RegistryService());
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var first = await service.ShowAsync("Windows-mcp test", "C-4 integration test toast");
        var second = await service.ShowAsync("Windows-mcp test", "C-4 integration test toast (2)");

        clock.Stop();
        first.Shown.Should().BeTrue("the in-process WinRT route must actually accept the toast");
        first.AppId.Should().Be("Windows-MCP");
        second.Registered.Should().BeTrue(
            "the service registered its own id under HKCU before the first show");
        using var key = OpenRegistration();
        key.Should().NotBeNull("the registration is what makes an unpackaged exe's toasts land");
        key!.GetValue("DisplayName").Should().Be("Windows-MCP");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "two toasts through a PowerShell cold start could not finish this fast; the route is in-process");
    }
}

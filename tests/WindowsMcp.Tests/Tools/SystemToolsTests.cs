using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class SystemToolsTests
{
    private static SystemTools MakeTools(
        IWmiService? wmi = null,
        IEnvService? env = null,
        IPowerService? power = null,
        INotificationService? notification = null,
        IAudioService? audio = null,
        ISecurityService? security = null,
        IReliabilityService? reliability = null,
        IDriverService? drivers = null,
        ISessionService? session = null)
    {
        return new SystemTools(
            wmi          ?? new Mock<IWmiService>().Object,
            env          ?? new Mock<IEnvService>().Object,
            power        ?? new Mock<IPowerService>().Object,
            notification ?? new Mock<INotificationService>().Object,
            audio        ?? new Mock<IAudioService>().Object,
            security     ?? new Mock<ISecurityService>().Object,
            reliability  ?? new Mock<IReliabilityService>().Object,
            drivers      ?? new Mock<IDriverService>().Object,
            session      ?? new Mock<ISessionService>().Object);
    }

    [Fact]
    public async Task PowerAction_requires_confirm()
    {
        var mockPower = new Mock<IPowerService>();
        var tools = MakeTools(power: mockPower.Object);

        Func<Task> act = () => tools.PowerAction("shutdown", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockPower.Verify(s => s.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SystemInfo_dispatches_to_wmi_with_correct_class()
    {
        var mockWmi = new Mock<IWmiService>();
        mockWmi.Setup(s => s.QueryAsync("Win32_OperatingSystem", null, null, It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<object>());

        var tools = MakeTools(wmi: mockWmi.Object);
        var result = await tools.SystemInfo("os");

        result.Should().NotBeNull();
        mockWmi.Verify(s => s.QueryAsync("Win32_OperatingSystem", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Env_set_requires_confirm()
    {
        var mockEnv = new Mock<IEnvService>();
        var tools = MakeTools(env: mockEnv.Object);

        Func<Task> act = () => tools.Env("set", name: "MY_VAR", value: "hello", confirm: false);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*confirm*");
        mockEnv.Verify(s => s.SetAsync(
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<EnvironmentVariableTarget>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- C-4: the notification tool ----------------------------------------------------------

    /// <summary>C-4 (R3): the advertised default is the id the server registers for itself.</summary>
    [Fact]
    public void Notification_defaults_app_id_to_the_servers_own_aumid()
    {
        typeof(SystemTools).GetMethod(nameof(SystemTools.Notification))!
            .GetParameters().Single(p => p.Name == "app_id")
            .DefaultValue.Should().Be("Windows-MCP");
    }

    /// <summary>C-4 (R3): the default reaches the service, and the result is the JSON contract.</summary>
    [Fact]
    public async Task Notification_shows_under_the_default_id_and_returns_the_result()
    {
        var mock = new Mock<INotificationService>();
        mock.Setup(s => s.ShowAsync("hi", "there", "Windows-MCP", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationResult(true, "Windows-MCP", true, null));
        var tools = MakeTools(notification: mock.Object);

        var json = JsonDocument.Parse(await tools.Notification("hi", "there")).RootElement;

        json.GetProperty("shown").GetBoolean().Should().BeTrue();
        json.GetProperty("appId").GetString().Should().Be("Windows-MCP");
        json.GetProperty("registered").GetBoolean().Should().BeTrue();
        mock.Verify(s => s.ShowAsync("hi", "there", "Windows-MCP", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// C-4 (R3): a caller's id is forwarded as given, and a dropped toast comes back as a result
    /// with the reason - not as a success and not as an exception.
    /// </summary>
    [Fact]
    public async Task Notification_forwards_a_custom_id_and_carries_the_note_back()
    {
        var mock = new Mock<INotificationService>();
        mock.Setup(s => s.ShowAsync("t", "m", "Contoso.App", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationResult(false, "Contoso.App", false,
                "Contoso.App is not a registered AppUserModelId (0x80070490)"));
        var tools = MakeTools(notification: mock.Object);

        var json = JsonDocument.Parse(await tools.Notification("t", "m", "Contoso.App")).RootElement;

        json.GetProperty("shown").GetBoolean().Should().BeFalse();
        json.GetProperty("appId").GetString().Should().Be("Contoso.App");
        json.GetProperty("registered").GetBoolean().Should().BeFalse();
        json.GetProperty("note").GetString().Should().Contain("Contoso.App");
        mock.Verify(s => s.ShowAsync("t", "m", "Contoso.App", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>C-4 (R3): a blank id never reaches the platform.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Notification_refuses_a_blank_app_id(string appId)
    {
        var mock = new Mock<INotificationService>();
        var tools = MakeTools(notification: mock.Object);

        Func<Task> act = () => tools.Notification("t", "m", appId);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*app*");
        mock.Verify(s => s.ShowAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- E-8: system_info(category:"session") --------------------------------------------------
    // docs/design/E-8-session-state.md "Surfaces": the SessionState as JSON (PascalCase, as the
    // record), no WMI query, the other categories unchanged, and the unknown-category refusal
    // lists session too.

    /// <summary>Every property of the record, in declaration order: what the JSON must carry, no more.</summary>
    private static readonly string[] SessionStateProperties =
    [
        "Elevated", "IntegrityLevel", "UserName", "SessionId", "Interactive",
        "ConnectState", "Locked", "InputDesktop", "InputDesktopAccess", "InputDesktopError", "InputAvailable", "Note",
    ];

    /// <summary>A secure-desktop state: every kind of value, and the two nullable ones null.</summary>
    private static readonly SessionState SecureDesktop = new(
        Elevated: true, IntegrityLevel: "High", UserName: "CONTOSO\\alice", SessionId: 3, Interactive: true,
        ConnectState: "Active", Locked: null, InputDesktop: null, InputDesktopAccess: "Denied", InputDesktopError: 5, InputAvailable: false,
        Note: "A secure desktop has the input.");

    private static Mock<ISessionService> SessionMock(SessionState state)
    {
        var mock = new Mock<ISessionService>();
        mock.Setup(s => s.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(state);
        return mock;
    }

    [Fact]
    public async Task SystemInfo_session_returns_the_session_state_as_pascal_case_json()
    {
        var session = SessionMock(SecureDesktop);
        var tools = MakeTools(session: session.Object);

        using var doc = JsonDocument.Parse(await tools.SystemInfo("session"));
        var json = doc.RootElement;

        json.ValueKind.Should().Be(JsonValueKind.Object, "one state, not a WMI row array");
        json.GetProperty("Elevated").GetBoolean().Should().BeTrue();
        json.GetProperty("IntegrityLevel").GetString().Should().Be("High");
        json.GetProperty("UserName").GetString().Should().Be("CONTOSO\\alice");
        json.GetProperty("SessionId").GetInt32().Should().Be(3);
        json.GetProperty("Interactive").GetBoolean().Should().BeTrue();
        json.GetProperty("ConnectState").GetString().Should().Be("Active");
        json.GetProperty("Locked").ValueKind.Should().Be(JsonValueKind.Null, "null means Windows did not say");
        json.GetProperty("InputDesktop").ValueKind.Should().Be(JsonValueKind.Null, "null is what a secure desktop looks like");
        json.GetProperty("InputDesktopAccess").GetString().Should().Be("Denied", "E-8 R2: why it is null");
        json.GetProperty("InputDesktopError").GetInt32().Should().Be(5, "E-8 R3: the Win32 error a live check reads");
        json.GetProperty("InputAvailable").GetBoolean().Should().BeFalse();
        json.GetProperty("Note").GetString().Should().Be("A secure desktop has the input.");
    }

    [Fact]
    public async Task SystemInfo_session_json_has_exactly_the_records_properties()
    {
        // Resolved ambiguity (test-agent): "as the record" is read as all ten properties, nulls
        // included - unlike SnapshotResult.SessionNote, which the note says to omit when null.
        var available = SecureDesktop with { InputDesktop = "Default", InputDesktopAccess = "Opened", InputDesktopError = null, Locked = false, InputAvailable = true, Note = null };
        var tools = MakeTools(session: SessionMock(available).Object);

        using var doc = JsonDocument.Parse(await tools.SystemInfo("session"));
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        names.Should().BeEquivalentTo(SessionStateProperties, "PascalCase, as the record, nothing added or dropped");
        names.Should().BeEquivalentTo(typeof(SessionState).GetProperties().Select(p => p.Name),
            "and the record and the wire agree");
        doc.RootElement.GetProperty("Note").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("InputDesktopError").ValueKind.Should().Be(JsonValueKind.Null,
            "E-8 R3: null is written, not omitted - twelve properties, always");
        doc.RootElement.GetProperty("Locked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SystemInfo_session_asks_the_session_service_once_with_the_calls_token_and_makes_no_wmi_query()
    {
        var wmi = new Mock<IWmiService>(MockBehavior.Strict);
        var session = SessionMock(SecureDesktop);
        var tools = MakeTools(wmi: wmi.Object, session: session.Object);
        using var cts = new CancellationTokenSource();

        await tools.SystemInfo("session", cts.Token);

        session.Verify(s => s.GetAsync(cts.Token), Times.Once);
        session.VerifyNoOtherCalls();
        wmi.Verify(w => w.QueryAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "session is not a WMI class");
    }

    [Theory]
    [InlineData("SESSION")]
    [InlineData("Session")]
    [InlineData("sEsSiOn")]
    public async Task SystemInfo_session_matches_the_category_case_insensitively(string category)
    {
        var session = SessionMock(SecureDesktop);
        var tools = MakeTools(session: session.Object);

        using var doc = JsonDocument.Parse(await tools.SystemInfo(category));

        doc.RootElement.GetProperty("IntegrityLevel").GetString().Should().Be("High");
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("os", "Win32_OperatingSystem")]
    [InlineData("memory", "Win32_PhysicalMemory")]
    [InlineData("disk", "Win32_LogicalDisk")]
    [InlineData("gpu", "Win32_VideoController")]
    [InlineData("battery", "Win32_Battery")]
    [InlineData("OS", "Win32_OperatingSystem")]
    public async Task SystemInfo_other_categories_still_query_wmi_and_never_the_session(string category, string wmiClass)
    {
        var wmi = new Mock<IWmiService>();
        wmi.Setup(s => s.QueryAsync(wmiClass, null, null, It.IsAny<CancellationToken>()))
           .ReturnsAsync([new Dictionary<string, object?> { ["Caption"] = "row" }]);
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        var tools = MakeTools(wmi: wmi.Object, session: session.Object);

        var result = await tools.SystemInfo(category);

        result.Should().Contain("\"Caption\":\"row\"", "the WMI rows are returned exactly as before E-8");
        wmi.Verify(s => s.QueryAsync(wmiClass, null, null, It.IsAny<CancellationToken>()), Times.Once);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("")]
    [InlineData(" session")]
    [InlineData("cpu")]
    public async Task SystemInfo_unknown_category_refusal_lists_session_with_the_others(string category)
    {
        var wmi = new Mock<IWmiService>(MockBehavior.Strict);
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        var tools = MakeTools(wmi: wmi.Object, session: session.Object);

        Func<Task> act = () => tools.SystemInfo(category);

        var message = (await act.Should().ThrowAsync<ArgumentException>()).Which.Message;
        message.Should().Contain($"'{category}'", "the refusal names what was asked for");
        // Judged on the rest of the message: 'sessions' echoed back must not pass for the list.
        var list = message.Replace($"'{category}'", "", StringComparison.Ordinal);
        foreach (var known in new[] { "os", "memory", "disk", "gpu", "battery", "session" })
            list.Should().Contain(known, "the refusal lists every category, session included");
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SystemInfo_null_category_is_a_caller_facing_refusal_listing_every_category()
    {
        // Before E-8 a null category reached Dictionary.TryGetValue and died as a masked
        // ArgumentNullException; now it is the same named refusal as any other unknown category.
        var wmi = new Mock<IWmiService>(MockBehavior.Strict);
        var session = new Mock<ISessionService>(MockBehavior.Strict);
        var tools = MakeTools(wmi: wmi.Object, session: session.Object);

        Func<Task> act = () => tools.SystemInfo(null!);

        var ex = (await act.Should().ThrowAsync<ArgumentException>()).Which;
        ex.Should().NotBeOfType<ArgumentNullException>("the refusal is the named one, not a framework null check");
        foreach (var known in new[] { "os", "memory", "disk", "gpu", "battery", "session" })
            ex.Message.Should().Contain(known);
        session.Verify(s => s.GetAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SystemInfo_description_advertises_the_session_category_and_what_it_reports()
    {
        var method = typeof(SystemTools).GetMethod(nameof(SystemTools.SystemInfo))!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        var parameter = method.GetParameters().Single(p => p.Name == "category")
            .GetCustomAttribute<DescriptionAttribute>()!.Description;

        description.Should().Contain("session");
        foreach (var fragment in new[] { "elevat", "integrity", "lock", "desktop" })
            description.Should().ContainEquivalentOf(fragment, "the description says what session reports");
        parameter.Should().Contain("session", "the schema's own parameter text lists it too");
    }

    [Fact]
    public void SystemInfo_description_names_InputDesktopError()
        // E-8 R3 decision B: the field a live check reads to confirm the secure-desktop signature.
        => typeof(SystemTools).GetMethod(nameof(SystemTools.SystemInfo))!
            .GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Contain("InputDesktopError");

    // ---- E-8 R2 decision 5: what Elevated and IntegrityLevel mean, and what is NOT detected ----

    private static string SystemInfoDescription() => typeof(SystemTools).GetMethod(nameof(SystemTools.SystemInfo))!
        .GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void SystemInfo_description_defines_Elevated_as_the_Administrators_role()
        // Elevated is not "can drive elevated windows" and not "is an admin user": it is the token.
        => SystemInfoDescription().Should().MatchRegex(@"(?i)Elevated[^.]*\bAdministrators?\b[^.]*\b(role|token)\b",
            "Elevated is defined, in one sentence, as the token being in the Administrators role / a full administrator token");

    [Fact]
    public void SystemInfo_description_says_IntegrityLevel_decides_whether_elevated_windows_can_be_driven()
        => SystemInfoDescription().Should().MatchRegex(@"(?i)IntegrityLevel[^.]*\belevated\b[^.]*\b(driv\w*|input)",
            "a Medium server's input to a High window is dropped by Windows - that is what IntegrityLevel tells the model");

    [Theory]
    [InlineData(@"(?i)UAC[^.]*not[^.]*secure desktop|secure desktop[^.]*UAC", "a UAC prompt configured not to use the secure desktop")]
    [InlineData(@"(?i)elevated[^.]*foreground|foreground[^.]*elevated", "an elevated foreground window")]
    [InlineData(@"(?i)minimi[sz]ed[^.]*remote|remote[^.]*minimi[sz]ed", "a minimised remote-desktop client")]
    public void SystemInfo_description_names_each_case_it_does_not_detect(string pattern, string limit)
    {
        var description = SystemInfoDescription();

        description.Should().MatchRegex(@"(?i)not detected|cannot detect|does not detect|doesn't detect|undetected|not reported",
            "the limits are stated as limits");
        description.Should().MatchRegex(pattern, $"the description names this limit: {limit}");
    }
}

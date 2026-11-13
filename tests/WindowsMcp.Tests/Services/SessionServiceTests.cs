using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-8: <see cref="SessionService"/> over a fake <c>ISessionProbe</c> - the DTO mapping field by
/// field, each read failing on its own, and the note rules applied to what was read. The probe is
/// the only thing that touches Windows, so everything here is pure.
/// </summary>
/// <remarks>
/// Unknown values on a throwing read (test-agent's choice where the design note is silent):
/// <list type="bullet">
/// <item><c>Elevated</c> -> false: what StartupReportService's private check already returned from
/// its catch, and the only claim that does not overstate the server's rights.</item>
/// <item><c>Interactive</c> -> true: a false here fires rule 4 ("no interactive desktop") and makes
/// every snapshot carry a warning on a read that merely failed - the note's own principle is
/// "unknown is not a reason to cry wolf". (Environment.UserInteractive does not throw in practice.)</item>
/// <item>The rest as the note lists them: IntegrityLevel/ConnectState "Unknown", UserName "",
/// SessionId -1, Locked null, InputDesktop null with InputDesktopAccess "Unknown" (R2 decision 3: a
/// desktop that could not be READ is not a secure desktop and raises no note).</item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public class SessionServiceTests
{
    /// <summary>
    /// A hand-written probe (ISessionProbe is internal, so Moq's proxy cannot reach it): each member
    /// is a settable function and every call is counted.
    /// </summary>
    private sealed class FakeProbe : ISessionProbe
    {
        public Func<bool> Elevated { get; set; } = () => false;
        public Func<uint> Rid { get; set; } = () => 0x2000;
        public Func<string> User { get; set; } = () => "CONTOSO\\alice";
        public Func<int> Session { get; set; } = () => 1;
        public Func<bool> Interactive { get; set; } = () => true;
        public Func<int> Connect { get; set; } = () => 0;         // WTSActive
        public Func<int> Flags { get; set; } = () => 1;           // WTS_SESSIONSTATE_UNLOCK
        public Func<(string? Name, int Error)> Desktop { get; set; } = () => ("Default", 0);

        public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

        private T Count<T>(string name, Func<T> read)
        {
            Calls[name] = Calls.GetValueOrDefault(name) + 1;
            return read();
        }

        public bool IsElevated() => Count(nameof(IsElevated), Elevated);
        public uint IntegrityRid() => Count(nameof(IntegrityRid), Rid);
        public string UserName() => Count(nameof(UserName), User);
        public int SessionId() => Count(nameof(SessionId), Session);
        public bool UserInteractive() => Count(nameof(UserInteractive), Interactive);
        public int ConnectState() => Count(nameof(ConnectState), Connect);
        public int SessionFlags() => Count(nameof(SessionFlags), Flags);
        public (string? Name, int Error) InputDesktop() => Count(nameof(InputDesktop), Desktop);

        public static readonly string[] Members =
        [
            nameof(IsElevated), nameof(IntegrityRid), nameof(UserName), nameof(SessionId),
            nameof(UserInteractive), nameof(ConnectState), nameof(SessionFlags), nameof(InputDesktop),
        ];

        /// <summary>Makes the member named <paramref name="member"/> throw <paramref name="ex"/>.</summary>
        public FakeProbe Failing(string member, Exception ex)
        {
            switch (member)
            {
                case nameof(IsElevated): Elevated = () => throw ex; break;
                case nameof(IntegrityRid): Rid = () => throw ex; break;
                case nameof(UserName): User = () => throw ex; break;
                case nameof(SessionId): Session = () => throw ex; break;
                case nameof(UserInteractive): Interactive = () => throw ex; break;
                case nameof(ConnectState): Connect = () => throw ex; break;
                case nameof(SessionFlags): Flags = () => throw ex; break;
                case nameof(InputDesktop): Desktop = () => throw ex; break;
                default: throw new ArgumentOutOfRangeException(nameof(member), member, "no such probe member");
            }
            return this;
        }
    }

    private static Task<SessionState> Get(FakeProbe probe, CancellationToken ct = default)
        => new SessionService(probe).GetAsync(ct);

    /// <summary>What the default <see cref="FakeProbe"/> reads as: an ordinary unelevated desktop session.</summary>
    private static readonly SessionState Ordinary = new(
        Elevated: false, IntegrityLevel: "Medium", UserName: "CONTOSO\\alice", SessionId: 1, Interactive: true,
        ConnectState: "Active", Locked: false, InputDesktop: "Default", InputDesktopAccess: "Opened", InputDesktopError: null,
        InputAvailable: true, Note: null);

    // ---- the mapping, field by field ----------------------------------------------------------

    [Fact]
    public async Task GetAsync_maps_an_ordinary_session_field_by_field()
        => (await Get(new FakeProbe())).Should().Be(Ordinary);

    [Fact]
    public async Task GetAsync_maps_an_elevated_session_on_another_session_id()
    {
        // Every field different from Ordinary, so a field wired to a constant cannot pass both.
        var probe = new FakeProbe
        {
            Elevated = () => true,
            Rid = () => 0x3000,
            User = () => "WORKGROUP\\admin",
            Session = () => 7,
            Interactive = () => true,
            Connect = () => 1,
            Flags = () => 1,
            Desktop = () => ("default", 0),
        };

        (await Get(probe)).Should().Be(new SessionState(
            Elevated: true, IntegrityLevel: "High", UserName: "WORKGROUP\\admin", SessionId: 7, Interactive: true,
            ConnectState: "Connected", Locked: false, InputDesktop: "default", InputDesktopAccess: "Opened", InputDesktopError: null,
            InputAvailable: true, Note: null));
    }

    [Fact]
    public async Task GetAsync_reports_the_input_desktop_name_as_read_not_normalised()
    {
        var probe = new FakeProbe { Desktop = () => ("Screen-saver", 0) };

        var state = await Get(probe);

        state.InputDesktop.Should().Be("Screen-saver");
        state.InputAvailable.Should().BeFalse();
        state.Note.Should().Contain("Screen-saver");
    }

    [Fact]
    public async Task GetAsync_applies_the_note_rules_to_what_it_read()
    {
        // Disconnected AND locked AND a secure desktop: locked wins, through the service as in SessionNotes.
        var probe = new FakeProbe { Connect = () => 4, Flags = () => 0, Desktop = () => (null, 5) };

        var state = await Get(probe);

        state.ConnectState.Should().Be("Disconnected");
        state.Locked.Should().BeTrue();
        state.InputDesktop.Should().BeNull();
        state.InputDesktopAccess.Should().Be("Denied");
        state.InputAvailable.Should().BeFalse();
        state.Note.Should().Be(SessionNotesTests.LockedNote);
    }

    [Fact]
    public async Task GetAsync_a_null_input_desktop_is_the_secure_desktop()
    {
        // R3 decision B: Denied is exactly ERROR_ACCESS_DENIED (5) from OpenInputDesktop.
        var state = await Get(new FakeProbe { Desktop = () => (null, 5) });

        state.InputDesktop.Should().BeNull();
        state.InputDesktopAccess.Should().Be("Denied");
        state.InputDesktopError.Should().Be(5, "the error is what a live check reads");
        state.InputAvailable.Should().BeFalse();
        state.Note.Should().Be(SessionNotesTests.SecureDesktopNote);
    }

    // ---- R3 decision B: the open failure's error is observable; only 5 is Denied ----------------

    [Fact]
    public async Task GetAsync_an_opened_input_desktop_is_Opened_with_no_error()
    {
        var state = await Get(new FakeProbe { Desktop = () => ("Default", 0) });

        state.InputDesktop.Should().Be("Default");
        state.InputDesktopAccess.Should().Be("Opened");
        state.InputDesktopError.Should().BeNull();
        state.Note.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]       // a failure that left no error code
    [InlineData(1)]       // ERROR_INVALID_FUNCTION
    [InlineData(6)]       // ERROR_INVALID_HANDLE
    [InlineData(1314)]    // ERROR_PRIVILEGE_NOT_HELD
    [InlineData(-1)]
    public async Task GetAsync_any_other_open_failure_is_Unknown_with_its_error_and_no_note(int error)
    {
        var state = await Get(new FakeProbe { Desktop = () => (null, error) });

        state.InputDesktop.Should().BeNull();
        state.InputDesktopAccess.Should().Be("Unknown", "only ERROR_ACCESS_DENIED is the secure-desktop signature");
        state.InputDesktopError.Should().Be(error);
        state.InputAvailable.Should().BeTrue("an unexplained failure is not a reason to tell the model to stop");
        state.Note.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_a_denied_input_desktop_records_error_5_and_the_secure_desktop_note()
        => (await Get(new FakeProbe { Desktop = () => (null, 5) })).Should().Be(Ordinary with
        {
            InputDesktop = null, InputDesktopAccess = "Denied", InputDesktopError = 5,
            InputAvailable = false, Note = SessionNotesTests.SecureDesktopNote,
        });

    [Fact]
    public async Task GetAsync_a_desktop_whose_name_cannot_be_read_is_Unknown_with_no_error_and_no_note()
    {
        // The probe throws only when the desktop OPENED but its name could not be read: no open error.
        var probe = new FakeProbe().Failing(nameof(ISessionProbe.InputDesktop), new InvalidOperationException("GetUserObjectInformation failed (Win32 error 87)."));

        var state = await Get(probe);

        state.InputDesktopAccess.Should().Be("Unknown");
        state.InputDesktopError.Should().BeNull();
        state.Note.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_an_opened_desktop_with_an_empty_name_is_Opened_and_not_the_users()
    {
        // test-agent's answer to ("", 0): Opened (it did open), the name "" as read, and since "" is
        // not Default the desktop is not the user's. The note must still read sensibly (SessionNotesTests).
        var state = await Get(new FakeProbe { Desktop = () => ("", 0) });

        state.InputDesktop.Should().Be("");
        state.InputDesktopAccess.Should().Be("Opened");
        state.InputDesktopError.Should().BeNull();
        state.InputAvailable.Should().BeFalse();
        state.Note.Should().NotBeNull().And.NotContain("''");
    }

    [Fact]
    public async Task GetAsync_a_process_without_a_window_station_says_so()
    {
        // R2 decision 2 (REVERSES R1's order): session 0 - a service, a scheduled task, OpenSSH - reports
        // Disconnected and often an unreadable input desktop; the note is still "no interactive desktop",
        // never "reconnect".
        var state = await Get(new FakeProbe { Interactive = () => false, Connect = () => 4, Session = () => 0, Desktop = () => (null, 1) });

        state.Interactive.Should().BeFalse();
        state.InputAvailable.Should().BeFalse();
        state.Note.Should().Be(SessionNotesTests.NotInteractiveNote);
    }

    [Theory]
    [InlineData(0x1000u, "Low")]
    [InlineData(0x2000u, "Medium")]
    [InlineData(0x4000u, "System")]
    public async Task GetAsync_names_the_integrity_rid_through_IntegrityLevels(uint rid, string expected)
        => (await Get(new FakeProbe { Rid = () => rid })).IntegrityLevel.Should().Be(expected);

    // ---- ConnectState: the WTS_CONNECTSTATE_CLASS member name without the WTS prefix ----------

    [Theory]
    [InlineData(0, "Active")]
    [InlineData(1, "Connected")]
    [InlineData(2, "ConnectQuery")]
    [InlineData(3, "Shadow")]
    [InlineData(4, "Disconnected")]
    [InlineData(5, "Idle")]
    [InlineData(6, "Listen")]
    [InlineData(7, "Reset")]
    [InlineData(8, "Down")]
    [InlineData(9, "Init")]
    [InlineData(10, "Unknown")]
    [InlineData(-1, "Unknown")]
    [InlineData(int.MaxValue, "Unknown")]
    public async Task GetAsync_names_the_connect_state_without_the_WTS_prefix(int raw, string expected)
        => (await Get(new FakeProbe { Connect = () => raw })).ConnectState.Should().Be(expected);

    // ---- Locked: WTSINFOEX_LEVEL1.SessionFlags ----------------------------------------------------

    [Theory]
    [InlineData(0, true)]                          // WTS_SESSIONSTATE_LOCK
    [InlineData(1, false)]                         // WTS_SESSIONSTATE_UNLOCK
    [InlineData(-1, null)]                         // WTS_SESSIONSTATE_UNKNOWN (0xFFFFFFFF)
    [InlineData(2, null)]
    [InlineData(int.MaxValue, null)]
    [InlineData(int.MinValue, null)]
    public async Task GetAsync_reads_locked_from_the_session_flags(int flags, bool? expected)
        => (await Get(new FakeProbe { Flags = () => flags })).Locked.Should().Be(expected);

    [Fact]
    public async Task GetAsync_an_unknown_lock_state_alone_leaves_input_available()
    {
        var state = await Get(new FakeProbe { Flags = () => unchecked((int)0xFFFFFFFF) });

        state.Locked.Should().BeNull();
        state.InputAvailable.Should().BeTrue("unknown is not a reason to cry wolf");
        state.Note.Should().BeNull();
    }

    // ---- each read fails on its own ------------------------------------------------------------

    public static TheoryData<string> ProbeMembers => new(FakeProbe.Members);

    /// <summary>Ordinary with the one field that <paramref name="member"/> feeds set to its unknown value.</summary>
    private static SessionState OrdinaryWithUnknown(string member) => member switch
    {
        nameof(ISessionProbe.IsElevated) => Ordinary with { Elevated = false },
        nameof(ISessionProbe.IntegrityRid) => Ordinary with { IntegrityLevel = "Unknown" },
        nameof(ISessionProbe.UserName) => Ordinary with { UserName = "" },
        nameof(ISessionProbe.SessionId) => Ordinary with { SessionId = -1 },
        nameof(ISessionProbe.UserInteractive) => Ordinary with { Interactive = true },
        nameof(ISessionProbe.ConnectState) => Ordinary with { ConnectState = "Unknown" },
        nameof(ISessionProbe.SessionFlags) => Ordinary with { Locked = null },
        // R2 decision 3 (REVERSES R1): a desktop that could not be READ is Unknown, not the secure
        // desktop - and Unknown raises no note.
        nameof(ISessionProbe.InputDesktop) => Ordinary with { InputDesktop = null, InputDesktopAccess = "Unknown", InputDesktopError = null },
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "no such probe member"),
    };

    [Theory]
    [MemberData(nameof(ProbeMembers))]
    public async Task GetAsync_a_failing_read_yields_that_fields_unknown_value_and_leaves_the_rest(string member)
    {
        var probe = new FakeProbe().Failing(member, new Win32Exception(5, "Access is denied"));

        var state = await Get(probe);

        state.Should().Be(OrdinaryWithUnknown(member));
    }

    [Fact]
    public async Task GetAsync_a_null_user_name_from_the_probe_is_reported_as_empty()
        // UserName is non-nullable on the wire: "" is the documented unknown, never null.
        => (await Get(new FakeProbe { User = () => null! })).UserName.Should().Be("");

    [Fact]
    public async Task GetAsync_a_desktop_name_that_was_read_is_Opened()
        => (await Get(new FakeProbe { Desktop = () => ("Winlogon", 0) })).Should().Be(Ordinary with
        {
            InputDesktop = "Winlogon", InputDesktopAccess = "Opened",
            InputAvailable = false, Note = SessionNotesTests.SecureDesktopNote,
        });

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task GetAsync_an_unreadable_input_desktop_does_not_hide_the_lock_state(bool? lockedFlag)
    {
        // Unknown access raises no note itself, but rules 2 and 3 still apply around it.
        var probe = new FakeProbe { Flags = () => lockedFlag == true ? 0 : -1 }
            .Failing(nameof(ISessionProbe.InputDesktop), new InvalidOperationException("GetUserObjectInformation failed"));

        var state = await Get(probe);

        state.InputDesktopAccess.Should().Be("Unknown");
        state.Note.Should().Be(lockedFlag == true ? SessionNotesTests.LockedNote : null);
    }

    [Fact]
    public async Task GetAsync_a_failing_interactivity_read_does_not_claim_a_service_host()
    {
        // The Interactive choice made explicit: a failed read must not fire rule 4.
        var probe = new FakeProbe().Failing(nameof(ISessionProbe.UserInteractive), new InvalidOperationException("no window station"));

        var state = await Get(probe);

        state.Interactive.Should().BeTrue();
        state.InputAvailable.Should().BeTrue("a read that failed is not evidence of a service host");
    }

    [Theory]
    [MemberData(nameof(ProbeMembers))]
    public async Task GetAsync_reads_every_fact_even_when_one_read_fails(string member)
    {
        var probe = new FakeProbe().Failing(member, new UnauthorizedAccessException("denied"));

        await Get(probe);

        foreach (var m in FakeProbe.Members)
            probe.Calls.GetValueOrDefault(m).Should().Be(1, $"{m} is read once, independently of {member}");
    }

    [Fact]
    public async Task GetAsync_with_every_read_failing_still_returns_a_state()
    {
        var probe = new FakeProbe();
        var kinds = new Exception[]
        {
            new Win32Exception(5), new UnauthorizedAccessException(), new InvalidOperationException(),
            new NullReferenceException(), new EntryPointNotFoundException(), new Exception("anything"),
            new IOException(), new NotSupportedException(),
        };
        for (int i = 0; i < FakeProbe.Members.Length; i++)
            probe.Failing(FakeProbe.Members[i], kinds[i]);

        var state = await Get(probe);

        state.Should().Be(new SessionState(
            Elevated: false, IntegrityLevel: "Unknown", UserName: "", SessionId: -1, Interactive: true,
            ConnectState: "Unknown", Locked: null, InputDesktop: null, InputDesktopAccess: "Unknown", InputDesktopError: null,
            // R2 decision 3 (REVERSES R1): every read failing is "available, no note".
            InputAvailable: true, Note: null));
    }

    // ---- cancellation is the one exception -----------------------------------------------------

    [Fact]
    public async Task GetAsync_with_a_cancelled_token_throws_before_the_first_read()
    {
        var probe = new FakeProbe();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => Get(probe, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Calls.Should().BeEmpty("nothing is read for a call that was already cancelled");
    }

    [Fact]
    public async Task GetAsync_reads_each_fact_once_per_call()
    {
        var probe = new FakeProbe();
        var service = new SessionService(probe);

        await service.GetAsync();
        await service.GetAsync();

        foreach (var m in FakeProbe.Members)
            probe.Calls.GetValueOrDefault(m).Should().Be(2, $"{m}: one read per call, and nothing cached between calls (a lock can end)");
    }
}

/// <summary>
/// E-8 "Tests.": the real <see cref="SessionService"/> with the real <c>Win32SessionProbe</c> answers
/// on this machine without throwing. Read-only: no input, no desktop switch. Every assertion is
/// checked against an independent .NET read, or against "not unknown", so a probe that swallowed
/// every failure into unknown values (the disk_inspect reclaimable lesson) fails here.
/// </summary>
[Trait("Category", "Integration")]
public class SessionServiceIntegrationTests
{
    private static readonly string[] KnownLevels = ["Untrusted", "Low", "Medium", "MediumPlus", "High", "System", "Protected"];

    [Fact]
    public async Task GetAsync_answers_on_this_machine_with_real_values()
    {
        var state = await new SessionService().GetAsync();

        using var identity = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();

        state.UserName.Should().NotBeNullOrEmpty().And.Be(identity.Name);
        state.SessionId.Should().BeGreaterThanOrEqualTo(0).And.Be(self.SessionId);
        state.IntegrityLevel.Should().BeOneOf(KnownLevels);
        state.ConnectState.Should().NotBe("Unknown", "WTSQuerySessionInformation answers for the current session");
        state.Elevated.Should().Be(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            "the shared elevation check is the WindowsPrincipal administrator check");
        if (state.Elevated)
            state.IntegrityLevel.Should().BeOneOf(["High", "System", "Protected"], "an enabled Administrators group means a high-integrity token");
        state.Interactive.Should().Be(Environment.UserInteractive);
        state.InputAvailable.Should().Be(state.Note is null);
    }

    [Fact]
    public async Task GetAsync_from_an_interactive_test_host_sees_the_users_desktop()
    {
        var state = await new SessionService().GetAsync();

        if (!Environment.UserInteractive)
            return;   // a service-hosted runner has no desktop to see; the test above still ran

        if (state.Locked == true)
        {
            // The workstation was locked while the suite ran: that is the case E-8 exists to report.
            // A locked workstation's input is the Winlogon desktop, which this process cannot open -
            // so "locked" beside a readable Default desktop is a misread SessionFlags, not a lock.
            state.InputDesktop.Should().NotBe("Default", "Locked=true with the user's desktop taking input is self-contradictory");
            state.InputAvailable.Should().BeFalse();
            state.Note.Should().Be(SessionNotesTests.LockedNote);
            return;
        }

        state.InputDesktop.Should().Be("Default", "the test host's input desktop is the user's");
        state.InputDesktopAccess.Should().Be("Opened", "the name was read, so the desktop was opened");
        state.InputDesktopError.Should().BeNull("it opened: there is no error to report");
        state.InputAvailable.Should().BeTrue();
        state.Note.Should().BeNull();
    }
}

/// <summary>
/// E-8 GREEN: each <c>Win32SessionProbe</c> member on its own, against the real APIs. The service
/// turns any failure into an unknown value, so a probe member that ALWAYS failed (a wrong P/Invoke
/// signature, a wrong info class) would leave the service-level test above with only unknowns to
/// look at - these call the members directly, where a failure is an exception, and pin the raw
/// values to independent .NET reads or to each other. Read-only: nothing is switched or changed.
/// </summary>
[Trait("Category", "Integration")]
public class Win32SessionProbeIntegrationTests
{
    private readonly Win32SessionProbe _probe = new();

    [Fact]
    public void IsElevated_is_the_shared_check()
        => _probe.IsElevated().Should().Be(ProcessElevation.IsElevated());

    [Fact]
    public void IntegrityRid_is_a_real_mandatory_level_consistent_with_elevation()
    {
        var rid = _probe.IntegrityRid();

        rid.Should().BeInRange(0x1000u, 0x4FFFu, "a test host runs between Low and System; Untrusted or Protected means a misread SID");
        if (_probe.IsElevated())
            rid.Should().BeGreaterThanOrEqualTo(0x3000u, "an enabled Administrators group means a high-integrity token");
        (rid % 0x100).Should().Be(0u, "well-known levels are multiples of 0x100; anything else is a misread sub-authority");
    }

    [Fact]
    public void UserName_and_SessionId_match_the_framework()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var self = Process.GetCurrentProcess();

        _probe.UserName().Should().Be(identity.Name);
        _probe.SessionId().Should().Be(self.SessionId);
        _probe.UserInteractive().Should().Be(Environment.UserInteractive);
    }

    [Fact]
    public void ConnectState_is_a_WTS_CONNECTSTATE_CLASS_value_and_the_test_session_is_active()
    {
        var state = _probe.ConnectState();

        state.Should().BeInRange(0, 9);
        if (Environment.UserInteractive)
            state.Should().Be(0, "the session running an interactive test host is WTSActive");
    }

    [Fact]
    public void SessionFlags_reads_lock_or_unlock_and_agrees_with_the_input_desktop()
    {
        // WTSINFOEX_LEVEL1_W.SessionFlags is the THIRD field after the 8-aligned union start
        // (SessionId at 8, SessionState at 12, SessionFlags at 16). A read at 12 returns
        // SessionState (WTSActive = 0) - which looks exactly like WTS_SESSIONSTATE_LOCK - so the
        // range check alone cannot catch it: the cross-check with the input desktop can.
        var flags = _probe.SessionFlags();
        var desktop = _probe.InputDesktop().Name;

        flags.Should().BeOneOf([0, 1], "this machine's Windows reports the lock state of the console/RDP session");
        if (string.Equals(desktop, "Default", StringComparison.OrdinalIgnoreCase))
            flags.Should().Be(1, "the user's desktop has the input, so the session is not locked (WTS_SESSIONSTATE_UNLOCK)");
    }

    [Fact]
    public void InputDesktop_from_an_interactive_test_host_is_Default_with_no_error()
    {
        if (!Environment.UserInteractive) return;   // a service-hosted runner has no input desktop to open
        if (_probe.SessionFlags() == 0) return;     // locked during the run: the secure desktop is the answer

        _probe.InputDesktop().Should().Be(("Default", 0));
    }
}

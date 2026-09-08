using System.Reflection;
using FluentAssertions;
using ModelContextProtocol;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Tools;
using Xunit;

namespace WindowsMcp.Tests.Tools;

[Trait("Category", "Unit")]
public class ShellToolsTests
{
    // The SDK's NullProgress is internal, so tests use a tiny recording fake.
    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        public List<ProgressNotificationValue> Reports { get; } = new();
        public void Report(ProgressNotificationValue value) => Reports.Add(value);
    }

    private static readonly PSResult SampleResult =
        new(Success: true, Stdout: "ok", Stderr: "", ExitCode: 0, Errors: Array.Empty<string>());

    private static readonly JobInfo SampleJob = new(
        "j1", "running", 4242, "Start-Sleep 30", DateTime.UtcNow, null, null, 0, 0, 0, 0);

    private static ShellTools MakeTools(
        IPowerShellService? ps = null,
        IJobService? jobs = null,
        TimeSpan? heartbeatInterval = null)
    {
        return new ShellTools(
            ps ?? new Mock<IPowerShellService>().Object,
            jobs ?? new Mock<IJobService>().Object,
            heartbeatInterval ?? TimeSpan.FromMinutes(1));
    }

    /// <summary>A service whose two-argument (C-6) overload answers, so the tool has something to serialize.</summary>
    private static Mock<IPowerShellService> Service(PSResult? result = null)
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(s => s.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
          .ReturnsAsync(result ?? SampleResult);
        return ps;
    }

    private static ParameterInfo TimeoutParameter() =>
        typeof(ShellTools).GetMethod(nameof(ShellTools.Powershell))!
            .GetParameters().Single(p => p.Name == "timeout_seconds");

    /// <summary>
    /// The constructor DI actually uses. Every other test here takes the internal three-argument
    /// one to shorten the heartbeat, so without this row the production ctor — and the 10-second
    /// interval a client's request timeout depends on — is never executed by the suite at all.
    /// </summary>
    [Fact]
    public async Task The_production_constructor_builds_a_working_tool_with_the_ten_second_heartbeat()
    {
        var ps = Service();

        var tools = new ShellTools(ps.Object, new Mock<IJobService>().Object);

        var interval = typeof(ShellTools)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(TimeSpan))
            .GetValue(tools);
        interval.Should().Be(TimeSpan.FromSeconds(10),
            "the heartbeat has to be well inside a spec-compliant client's request timeout");

        var json = await tools.Powershell("'hi'", new RecordingProgress());

        json.Should().Contain("\"Stdout\":\"ok\"", "and the tool built that way still runs a command");
        ps.Verify(s => s.RunAsync("'hi'", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Powershell_fast_command_returns_result_without_heartbeats()
    {
        var ps = Service();
        var progress = new RecordingProgress();
        var tools = MakeTools(ps: ps.Object);

        var json = await tools.Powershell("'hi'", progress);

        json.Should().Contain("\"Stdout\":\"ok\"");
        progress.Reports.Should().BeEmpty("a fast command must not tick the heartbeat");
    }

    [Fact]
    public async Task Powershell_slow_command_emits_monotonic_heartbeats_and_still_returns_result()
    {
        var ps = new Mock<IPowerShellService>();
        ps.Setup(s => s.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
          .Returns(async (string _, TimeSpan? _, CancellationToken ct) =>
          {
              await Task.Delay(150, ct);
              return SampleResult;
          });
        var progress = new RecordingProgress();
        var tools = MakeTools(ps: ps.Object, heartbeatInterval: TimeSpan.FromMilliseconds(20));

        var json = await tools.Powershell("Start-Sleep 1", progress);

        json.Should().Contain("\"Stdout\":\"ok\"");
        progress.Reports.Should().NotBeEmpty("a long command must emit heartbeats");
        progress.Reports.Select(r => r.Progress).Should().BeInAscendingOrder(
            "the SDK requires Progress to increase monotonically");
        progress.Reports.Should().OnlyContain(r => r.Message != null && r.Message.Contains("powershell running"));
    }

    [Fact]
    public async Task Powershell_background_starts_a_job_and_never_runs_foreground()
    {
        var ps = Service();
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleJob);
        var tools = MakeTools(ps: ps.Object, jobs: jobs.Object);

        var json = await tools.Powershell("msiexec /i app.msi /qn", new RecordingProgress(), background: true);

        json.Should().Contain("\"Id\":\"j1\"").And.Contain("\"State\":\"running\"");
        jobs.Verify(j => j.StartAsync("msiexec /i app.msi /qn", It.IsAny<CancellationToken>()), Times.Once);
        ps.Verify(s => s.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- C-6 (roadmap R9): timeout_seconds ---------------------------------------------------

    [Fact]
    public void Powershell_timeout_seconds_defaults_to_zero_and_is_the_last_parameter_before_the_token()
    {
        var parameters = typeof(ShellTools).GetMethod(nameof(ShellTools.Powershell))!.GetParameters();
        var timeout = TimeoutParameter();

        timeout.HasDefaultValue.Should().BeTrue();
        timeout.DefaultValue.Should().Be(0, "0 means the execution backstop only - today's behaviour");
        parameters[Array.IndexOf(parameters, timeout) - 1].Name.Should().Be("background");
        parameters[Array.IndexOf(parameters, timeout) + 1].ParameterType.Should().Be<CancellationToken>();
    }

    [Fact]
    public void Powershell_description_tells_the_model_about_the_timeout()
    {
        var description = typeof(ShellTools).GetMethod(nameof(ShellTools.Powershell))!
            .GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;

        description.Should().Contain("timeout_seconds",
            "the description is the only spec the model reads; an unadvertised parameter is unused");
    }

    [Fact]
    public async Task Powershell_without_a_timeout_passes_null_to_the_service()
    {
        var ps = Service();
        var tools = MakeTools(ps: ps.Object);

        await tools.Powershell("'hi'", new RecordingProgress());

        ps.Verify(s => s.RunAsync("'hi'", null, It.IsAny<CancellationToken>()), Times.Once,
            "the tool always calls the two-argument overload; 0 seconds is a null TimeSpan");
    }

    [Theory]
    [InlineData(1)]     // the low end of the range, exactly
    [InlineData(5)]
    [InlineData(900)]   // the high end of the range, exactly
    public async Task Powershell_timeout_seconds_becomes_a_TimeSpan(int seconds)
    {
        var ps = Service();
        var tools = MakeTools(ps: ps.Object);

        await tools.Powershell("'hi'", new RecordingProgress(), timeout_seconds: seconds);

        ps.Verify(s => s.RunAsync("'hi'", TimeSpan.FromSeconds(seconds), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(901)]
    [InlineData(int.MaxValue)]
    public async Task Powershell_refuses_a_timeout_outside_the_range(int seconds)
    {
        var ps = Service();
        var tools = MakeTools(ps: ps.Object);

        Func<Task> act = () => tools.Powershell("'hi'", new RecordingProgress(), timeout_seconds: seconds);

        var refusal = await act.Should().ThrowAsync<ArgumentException>();
        refusal.WithMessage("*timeout_seconds*", "the refusal names the parameter");
        refusal.WithMessage("*900*", "and the range the caller has to stay inside");
        ps.Verify(s => s.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Powershell_refuses_a_timeout_together_with_background()
    {
        var ps = Service();
        var jobs = new Mock<IJobService>();
        var tools = MakeTools(ps: ps.Object, jobs: jobs.Object);

        Func<Task> act = () => tools.Powershell("msiexec /i app.msi /qn", new RecordingProgress(),
            background: true, timeout_seconds: 5);

        var refusal = await act.Should().ThrowAsync<ArgumentException>();
        refusal.WithMessage("*timeout_seconds*",
            "a job has job(cancel) and its own backstop; the refusal names both parameters");
        refusal.WithMessage("*background*", "and says which other parameter it conflicts with");
        jobs.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "the refusal comes before the job is started, not after");
        ps.Verify(s => s.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// C-6: the refusal is "timeout_seconds AND background", not "background". 0 is the default, so
    /// a client that fills every parameter in the schema — or a model that echoes the default back —
    /// must not be refused for asking for exactly what it would have got anyway.
    /// </summary>
    [Theory]
    [InlineData(true)]      // explicitly passed
    [InlineData(false)]     // left at its default
    public async Task Powershell_background_with_a_zero_timeout_is_allowed(bool passExplicitly)
    {
        var ps = Service();
        var jobs = new Mock<IJobService>();
        jobs.Setup(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleJob);
        var tools = MakeTools(ps: ps.Object, jobs: jobs.Object);

        var json = passExplicitly
            ? await tools.Powershell("msiexec /i app.msi /qn", new RecordingProgress(), background: true, timeout_seconds: 0)
            : await tools.Powershell("msiexec /i app.msi /qn", new RecordingProgress(), background: true);

        json.Should().Contain("\"Id\":\"j1\"", "the job started; 0 is not a timeout to conflict with");
        jobs.Verify(j => j.StartAsync("msiexec /i app.msi /qn", It.IsAny<CancellationToken>()), Times.Once);
        ps.Verify(s => s.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>C-6: the range check runs before the combination check, so a bad value is named as such.</summary>
    [Fact]
    public async Task Powershell_background_with_an_out_of_range_timeout_is_refused_for_the_range()
    {
        var jobs = new Mock<IJobService>();
        var tools = MakeTools(ps: Service().Object, jobs: jobs.Object);

        Func<Task> act = () => tools.Powershell("msiexec /i app.msi /qn", new RecordingProgress(),
            background: true, timeout_seconds: 901);

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*900*", "901 is out of range whatever background says; the value is the first problem");
        jobs.Verify(j => j.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Powershell_result_carries_the_timed_out_flag()
    {
        var timedOut = new PSResult(
            Success: false, Stdout: "before", Stderr: "", ExitCode: -1,
            Errors: ["timed out after 5s"], TimedOut: true);
        var tools = MakeTools(ps: Service(timedOut).Object);

        var json = await tools.Powershell("Start-Sleep 60", new RecordingProgress(), timeout_seconds: 5);

        json.Should().Contain("\"TimedOut\":true", "the caller needs to know the clock fired, not guess from ExitCode");
        json.Should().Contain("\"Stdout\":\"before\"").And.Contain("\"ExitCode\":-1");
        json.Should().Contain("timed out after 5s");
    }

    [Fact]
    public async Task Powershell_result_says_it_did_not_time_out_on_a_normal_run()
    {
        var tools = MakeTools(ps: Service().Object);

        var json = await tools.Powershell("'hi'", new RecordingProgress());

        json.Should().Contain("\"TimedOut\":false",
            "the field is on every result, so a client can read it without checking whether it is there");
    }

    // ---- F1: how much output was dropped ------------------------------------------------------

    /// <summary>
    /// F1: the foreground result is bounded like a job's, so — like a job's — it has to say what
    /// went. A client reading a capped <c>Stdout</c> with no count cannot tell a short answer from
    /// a truncated one, and the front of the output is where a script's own error message is.
    /// </summary>
    [Fact]
    public async Task Powershell_result_says_how_much_output_was_dropped()
    {
        var flooded = new PSResult(
            Success: true, Stdout: "the last million characters", Stderr: "", ExitCode: 0,
            Errors: Array.Empty<string>(), TimedOut: false,
            StdoutTrimmedChars: 606_400, StderrTrimmedChars: 12);
        var tools = MakeTools(ps: Service(flooded).Object);

        var json = await tools.Powershell("1..1600 | % { 'x'*1000 }", new RecordingProgress());

        json.Should().Contain("\"StdoutTrimmedChars\":606400",
            "the count is on the wire, not only in the service");
        json.Should().Contain("\"StderrTrimmedChars\":12");
    }

    [Fact]
    public async Task Powershell_result_reports_nothing_dropped_on_an_ordinary_run()
    {
        var tools = MakeTools(ps: Service().Object);

        var json = await tools.Powershell("'hi'", new RecordingProgress());

        json.Should().Contain("\"StdoutTrimmedChars\":0", "both counts are on every result");
        json.Should().Contain("\"StderrTrimmedChars\":0");
    }
}

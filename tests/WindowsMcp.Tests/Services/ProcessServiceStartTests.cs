using System.Diagnostics;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-11: <c>ProcessService.BuildStartInfo</c> - the pure half of <c>start_process</c>. Every
/// decision that used to be buried inside a spawn (which part of the string is the executable,
/// what goes to <c>ArgumentList</c> versus <c>Arguments</c>, whether the working directory
/// exists) is asserted here without starting anything, and the byte-identity of the old
/// <c>command</c>-only path is pinned so B-11 cannot quietly change it.
/// <para>
/// The live half - that a spec built this way really starts a process - is
/// <see cref="ProcessServiceStartSpecIntegrationTests"/> below.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class ProcessServiceStartTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";

    // ---- command only: today's behaviour, byte for byte -------------------------------------

    [Theory]
    // (command, expected FileName, expected Arguments)
    [InlineData("notepad.exe", "notepad.exe", "")]
    [InlineData("notepad.exe a.txt", "notepad.exe", "a.txt")]
    [InlineData("cmd.exe /c echo hi", "cmd.exe", "/c echo hi")]
    [InlineData("  notepad.exe  a.txt  ", "notepad.exe", "a.txt")]
    [InlineData("\"C:\\My App\\foo.exe\" arg1 arg2", "C:\\My App\\foo.exe", "arg1 arg2")]
    [InlineData("\"C:\\My App\\foo.exe\"", "C:\\My App\\foo.exe", "")]
    public void Without_an_argv_list_the_command_is_split_exactly_as_it_always_was(
        string command, string fileName, string arguments)
    {
        var psi = ProcessService.BuildStartInfo(new ProcessStart(command, null, null, false));

        psi.FileName.Should().Be(fileName, "the first-space / quoted-exe split is unchanged by B-11");
        psi.Arguments.Should().Be(arguments);
        psi.ArgumentList.Should().BeEmpty("the old path builds one argument string, not a list");
        psi.UseShellExecute.Should().BeFalse();
        psi.RedirectStandardOutput.Should().BeFalse();
        psi.WorkingDirectory.Should().BeEmpty("no cwd given means the server's own directory, as before");
    }

    [Fact]
    public void An_unmatched_opening_quote_is_still_an_ArgumentException()
    {
        var act = () => ProcessService.BuildStartInfo(new ProcessStart("\"C:\\nope\\foo.exe", null, null, false));

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("quote");
    }

    // ---- with an argv list -------------------------------------------------------------------

    [Fact]
    public void With_an_argv_list_the_command_is_the_executable_and_nothing_is_split()
    {
        // A path with a space and no quotes: the whole point of argv mode is that this is one
        // file name, not an executable plus an argument.
        var psi = ProcessService.BuildStartInfo(
            new ProcessStart(@"C:\My App\foo.exe", ["a b", "--x=\"y\"", "plain"], null, false));

        psi.FileName.Should().Be(@"C:\My App\foo.exe");
        psi.ArgumentList.Should().Equal("a b", "--x=\"y\"", "plain");
        psi.Arguments.Should().BeEmpty(
            "Arguments and ArgumentList are mutually exclusive in .NET: setting both throws at spawn time");
    }

    [Fact]
    public void An_empty_argv_list_still_means_the_command_is_an_executable()
    {
        var psi = ProcessService.BuildStartInfo(new ProcessStart(@"C:\My App\foo.exe", [], null, false));

        psi.FileName.Should().Be(@"C:\My App\foo.exe", "no split: an empty list is still argv mode");
        psi.ArgumentList.Should().BeEmpty();
        psi.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Argv_items_are_not_quoted_trimmed_or_reordered()
    {
        string[] args = ["  leading and trailing  ", "", "tab\there", "line\r\nbreak", "café"];

        var psi = ProcessService.BuildStartInfo(new ProcessStart(Cmd, args, null, false));

        psi.ArgumentList.Should().Equal(args);
    }

    // ---- cwd ---------------------------------------------------------------------------------

    [Fact]
    public void An_existing_cwd_becomes_the_working_directory()
    {
        var psi = ProcessService.BuildStartInfo(new ProcessStart(Cmd, null, Path.GetTempPath(), false));

        psi.WorkingDirectory.Should().Be(Path.GetTempPath());
    }

    [Fact]
    public void A_missing_cwd_is_refused_by_name_before_anything_is_spawned()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wmcp-b11-" + Guid.NewGuid().ToString("N"));

        var act = () => ProcessService.BuildStartInfo(new ProcessStart(Cmd, null, missing, false));

        act.Should().Throw<DirectoryNotFoundException>()
            .Which.Message.Should().Contain(missing,
                "the caller is told which directory it was, not just that one was missing");
    }

    [Fact]
    public void An_argv_list_and_a_cwd_together_are_both_honoured()
    {
        // The combination the tool actually sends most often (an exe, its arguments, and where to
        // run them), and the one place the two decisions could interfere: argv mode must not lose
        // the working directory, and the working directory must not push the command back through
        // the first-space split.
        var psi = ProcessService.BuildStartInfo(
            new ProcessStart(@"C:\My App\foo.exe", ["--out", "a b.txt"], Path.GetTempPath(), false));

        psi.FileName.Should().Be(@"C:\My App\foo.exe", "argv mode still means the command is the executable");
        psi.ArgumentList.Should().Equal("--out", "a b.txt");
        psi.Arguments.Should().BeEmpty();
        psi.WorkingDirectory.Should().Be(Path.GetTempPath());
    }

    [Fact]
    public void An_argv_list_with_a_missing_cwd_is_refused_before_the_argument_list_matters()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wmcp-b11-" + Guid.NewGuid().ToString("N"));

        var act = () => ProcessService.BuildStartInfo(new ProcessStart(Cmd, ["/c", "exit"], missing, false));

        act.Should().Throw<DirectoryNotFoundException>().Which.Message.Should().Contain(missing);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_cwd_is_treated_as_no_cwd(string cwd)
    {
        var psi = ProcessService.BuildStartInfo(new ProcessStart(Cmd, null, cwd, false));

        psi.WorkingDirectory.Should().BeEmpty("blank is 'not given', not 'a directory called nothing'");
    }

    [Fact]
    public void A_file_path_given_as_cwd_is_refused_like_a_missing_one()
    {
        var file = Path.Combine(Path.GetTempPath(), "wmcp-b11-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, "");
        try
        {
            var act = () => ProcessService.BuildStartInfo(new ProcessStart(Cmd, null, file, false));

            act.Should().Throw<DirectoryNotFoundException>().Which.Message.Should().Contain(file);
        }
        finally
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
    }

    // ---- use_shell_execute -------------------------------------------------------------------

    [Fact]
    public void UseShellExecute_is_carried_through()
    {
        var psi = ProcessService.BuildStartInfo(new ProcessStart("https://example.invalid", null, null, true));

        psi.UseShellExecute.Should().BeTrue();
        psi.FileName.Should().Be("https://example.invalid");
    }

    [Fact]
    public void UseShellExecute_defaults_to_false_for_the_plain_call()
    {
        ProcessService.BuildStartInfo(new ProcessStart(Cmd, null, null, false))
            .UseShellExecute.Should().BeFalse("the server does not want a shell between it and the child");
    }
}

/// <summary>
/// B-11 through a real spawn. <see cref="ProcessServiceStartTests"/> asserts what the
/// <see cref="ProcessStartInfo"/> looks like and would stay green if nothing ever used it - the
/// mocked-collaborator failure mode CLAUDE.md records. These start real, immediately-exiting
/// processes.
/// </summary>
[Trait("Category", "Integration")]
public class ProcessServiceStartSpecIntegrationTests : IDisposable
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wmcp-b11-" + Guid.NewGuid().ToString("N"));

    public ProcessServiceStartSpecIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static ProcessService Make() => new(new Mock<IWmiService>().Object);

    [Fact]
    public async Task StartDetachedAsync_starts_a_process_from_an_argv_spec()
    {
        var pid = await Make().StartDetachedAsync(new ProcessStart(Cmd, ["/c", "exit", "0"], null, false));

        pid.Should().BeGreaterThan(0, "the caller gets the pid of the process that was started");
    }

    [Fact]
    public async Task An_argv_item_containing_a_space_reaches_the_child_as_one_argument()
    {
        // The proof that ArgumentList carries the arguments and not a concatenated string: a
        // batch file writes its %1 and %2 to a file. Concatenation without quoting would make %1
        // "hello" and %2 "world"; ArgumentList makes %1 "hello world" and %2 "second".
        var output = Path.Combine(_dir, "argv.txt");
        var batch = Path.Combine(_dir, "argv.cmd");
        File.WriteAllLines(batch, ["@echo off", $"echo %~1>\"{output}\"", $"echo %~2>>\"{output}\""]);

        var pid = await Make().StartDetachedAsync(
            new ProcessStart(Cmd, ["/c", batch, "hello world", "second"], null, false));

        pid.Should().BeGreaterThan(0);
        await WaitForFile(output);
        // The expected sequence goes in as one collection: Equal(params object[]) would otherwise
        // read the because-string as a third expected element.
        File.ReadAllLines(output).Select(l => l.Trim()).Should().Equal(
            new[] { "hello world", "second" },
            "an argument with a space is one argument, unquoted by the caller and unsplit by us");
    }

    [Fact]
    public async Task The_child_runs_in_the_working_directory_it_was_given()
    {
        var output = Path.Combine(_dir, "cwd.txt");
        var batch = Path.Combine(_dir, "cwd.cmd");
        File.WriteAllLines(batch, ["@echo off", $"cd>\"{output}\""]);

        var pid = await Make().StartDetachedAsync(new ProcessStart(Cmd, ["/c", batch], _dir, false));

        pid.Should().BeGreaterThan(0);
        await WaitForFile(output);
        File.ReadAllText(output).Trim().Should().Be(_dir.TrimEnd(Path.DirectorySeparatorChar),
            "cwd is not decoration: the child really started there");
    }

    [Fact]
    public async Task A_missing_working_directory_is_refused_as_a_DirectoryNotFoundException()
    {
        // Our refusal, not Win32's: Process.Start on a bad WorkingDirectory throws a
        // Win32Exception saying "The directory name is invalid", which names nothing useful.
        var missing = Path.Combine(_dir, "not-here");
        var marker = Path.Combine(_dir, "should-not-exist.txt");
        var batch = Path.Combine(_dir, "marker.cmd");
        File.WriteAllLines(batch, ["@echo off", $"echo ran>\"{marker}\""]);

        var act = () => Make().StartDetachedAsync(new ProcessStart(Cmd, ["/c", batch], missing, false));

        (await act.Should().ThrowAsync<DirectoryNotFoundException>()).Which.Message.Should().Contain(missing);
        await Task.Delay(300);
        File.Exists(marker).Should().BeFalse("the refusal happened before anything was spawned");
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_spawn_before_it_happens()
    {
        // The marker file is the witness: a cancellation checked after Process.Start would leave
        // it behind.
        var marker = Path.Combine(_dir, "cancelled.txt");
        var batch = Path.Combine(_dir, "cancelled.cmd");
        File.WriteAllLines(batch, ["@echo off", $"echo ran>\"{marker}\""]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => Make().StartDetachedAsync(new ProcessStart(Cmd, ["/c", batch], null, false), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await Task.Delay(300);
        File.Exists(marker).Should().BeFalse("nothing was spawned, so nothing ran");
    }

    [Fact]
    public async Task The_command_only_overload_still_behaves_exactly_as_it_did()
    {
        // The compatibility guarantee, exercised through the real spawn the old tests used.
        var pid = await Make().StartDetachedAsync("\"C:\\Windows\\System32\\cmd.exe\" /c exit");

        pid.Should().BeGreaterThan(0);
    }

    private static async Task WaitForFile(string path)
    {
        for (int i = 0; i < 100 && !File.Exists(path); i++) await Task.Delay(50);
        File.Exists(path).Should().BeTrue($"the child was expected to write {path}");
        await Task.Delay(50);   // let the handle close before reading
    }
}

using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Startup;
using Xunit;

namespace WindowsMcp.Tests.Startup;

[Trait("Category", "Unit")]
public class StartupApprovalTests
{
    [Theory]
    [InlineData(2, true)]   // 0x02 enabled
    [InlineData(6, true)]   // 0x06 enabled
    [InlineData(3, false)]  // 0x03 disabled
    [InlineData(7, false)]  // 0x07 disabled
    public void IsEnabled_decodes_first_byte_parity(byte first, bool expected)
    {
        StartupApproval.IsEnabled(new byte[] { first, 0, 0, 0 }).Should().Be(expected);
    }

    [Fact]
    public void IsEnabled_treats_absent_or_empty_flag_as_enabled()
    {
        StartupApproval.IsEnabled(null).Should().BeTrue();
        StartupApproval.IsEnabled(Array.Empty<byte>()).Should().BeTrue();
    }
}

[Trait("Category", "Unit")]
public class CommandTargetTests
{
    [Fact]
    public void ResolveExe_handles_quoted_executable()
    {
        CommandTarget.ResolveExe("\"C:\\Program Files\\App\\foo.exe\" --arg")
            .Should().Be("C:\\Program Files\\App\\foo.exe");
    }

    [Fact]
    public void ResolveExe_handles_unquoted_path_with_spaces_via_exe_suffix()
    {
        CommandTarget.ResolveExe("C:\\Program Files\\Adobe\\Creative Cloud.exe --showwindow=false")
            .Should().Be("C:\\Program Files\\Adobe\\Creative Cloud.exe");
    }

    [Fact]
    public void ResolveExe_handles_bare_token()
    {
        CommandTarget.ResolveExe("MessengerHelper.exe --lassie").Should().Be("MessengerHelper.exe");
    }

    [Fact]
    public void ResolveExe_returns_null_for_blank()
    {
        CommandTarget.ResolveExe(null).Should().BeNull();
        CommandTarget.ResolveExe("   ").Should().BeNull();
    }

    [Fact]
    public void Exists_is_true_for_real_system_binary_and_false_for_missing()
    {
        CommandTarget.Exists($"\"{Path.Combine(Environment.SystemDirectory, "kernel32.dll")}\"").Should().BeTrue();
        CommandTarget.Exists("C:\\nope\\definitely_missing_wmcp.exe --x").Should().BeFalse();
    }

    [Fact]
    public void Exists_resolves_bare_exe_via_PATH_not_just_system32()
    {
        // powershell.exe is on PATH (System32\WindowsPowerShell\v1.0) but NOT directly in
        // System32 — the case that made ResumeClaudeCode report a missing target.
        CommandTarget.Exists("powershell.exe").Should().BeTrue();
        CommandTarget.Exists("definitely_missing_wmcp_bare.exe").Should().BeFalse();
    }
}

[Trait("Category", "Unit")]
public class StartupReportRendererTests
{
    [Fact]
    public void Render_includes_header_section_titles_and_entries()
    {
        var dto = ReportFixtures.Empty(
            processes: new[] { new ProcessEntry(123, "foo", "C:\\foo.exe", 10, true, null) },
            run: new[] { new RunEntry("HKCU", "Software\\...\\Run", "Zoom", "zoom.exe", false, true, false, null) },
            errors: new[] { "lsp: boom" });

        var text = StartupReportRenderer.Render(dto);

        text.Should().Contain("Windows-mcp Startup Report");
        text.Should().Contain("Elevated: True");
        text.Should().Contain("Boot: Normal");                       // enriched header
        text.Should().Contain("== Processes (1) ==");
        text.Should().Contain("Zoom = zoom.exe");
        text.Should().Contain("enabled=N");
        text.Should().Contain("== Image File Execution Options (0) =="); // a new section renders
        text.Should().Contain("== Errors (1) ==").And.Contain("lsp: boom");
    }
}

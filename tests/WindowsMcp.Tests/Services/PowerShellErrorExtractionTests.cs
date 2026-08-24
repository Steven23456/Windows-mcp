using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

// Pure tests of PowerShellService.ExtractErrors against real CLIXML shapes captured from
// Windows PowerShell 5.1 with redirected stderr (2026-08-24).
[Trait("Category", "Unit")]
public class PowerShellErrorExtractionTests
{
    private const string ProgressOnlyClixml =
        "#< CLIXML\n" +
        "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
        "<Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T>" +
        "<T>System.Object</T></TN><MS><I64 N=\"SourceId\">1</I64><PR N=\"Record\">" +
        "<AV>Preparing modules for first use.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC>" +
        "<T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj></Objs>";

    private const string ErrorClixml =
        "#< CLIXML\n" +
        "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
        "<S S=\"Error\">Write-Error 'boom'; 'after' : boom_x000D__x000A_</S>" +
        "<S S=\"Error\">    + CategoryInfo          : NotSpecified: (:) [Write-Error], WriteErrorException_x000D__x000A_</S>" +
        "<S S=\"Error\"> _x000D__x000A_</S></Objs>";

    [Fact]
    public void Progress_only_clixml_yields_no_errors()
    {
        PowerShellService.ExtractErrors(ProgressOnlyClixml).Should().BeEmpty(
            "progress records on stderr are benign, not errors");
    }

    [Fact]
    public void Warning_records_do_not_count_as_errors()
    {
        var clixml =
            "#< CLIXML\n" +
            "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<S S=\"warning\">careful</S></Objs>";
        PowerShellService.ExtractErrors(clixml).Should().BeEmpty();
    }

    [Fact]
    public void Error_records_are_extracted_and_clixml_escapes_decoded()
    {
        var errors = PowerShellService.ExtractErrors(ErrorClixml);

        errors.Should().NotBeEmpty();
        errors.Should().Contain(e => e.Contains("boom"));
        errors.Should().OnlyContain(e => !e.Contains("_x000D_"), "CLIXML escapes must be decoded");
        errors.Should().OnlyContain(e => !e.StartsWith("<"), "errors must be plain text, not XML");
    }

    [Fact]
    public void Mixed_progress_and_error_records_keep_only_the_errors()
    {
        var clixml =
            "#< CLIXML\n" +
            "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">" +
            "<Obj S=\"progress\" RefId=\"0\"><MS><PR N=\"Record\"><AV>Preparing modules for first use.</AV></PR></MS></Obj>" +
            "<S S=\"Error\">real failure_x000D__x000A_</S></Objs>";

        PowerShellService.ExtractErrors(clixml).Should().ContainSingle()
            .Which.Should().Be("real failure");
    }

    [Fact]
    public void Non_clixml_stderr_keeps_the_plain_line_split()
    {
        PowerShellService.ExtractErrors("native error line 1\nnative error line 2\n")
            .Should().Equal("native error line 1", "native error line 2");
    }

    [Fact]
    public void Unparseable_clixml_falls_back_to_raw_lines()
    {
        var mangled = "#< CLIXML\n<Objs Version=\"1.1.0.1\"><unclosed\nraw native noise";
        PowerShellService.ExtractErrors(mangled).Should().NotBeEmpty(
            "when the CLIXML cannot be parsed, be conservative and keep the old behavior");
    }

    [Fact]
    public void Empty_and_header_only_stderr_yield_no_errors()
    {
        PowerShellService.ExtractErrors("").Should().BeEmpty();
        PowerShellService.ExtractErrors("#< CLIXML\n").Should().BeEmpty();
    }
}

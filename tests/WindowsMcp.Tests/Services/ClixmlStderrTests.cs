using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// D-8: pure tests of the stderr decoder against real CLIXML shapes captured from Windows
/// PowerShell 5.1 with redirected stderr. These are the fast regression net for the decoding —
/// the real-process tests in <see cref="PowerShellServiceTests"/> only need to prove the two
/// layers are wired up.
/// </summary>
[Trait("Category", "Unit")]
public class ClixmlStderrTests
{
    private const string Header = "#< CLIXML\n";
    private const string Ns = "<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\">";

    // Each <Objs> body is kept without the header so they can be concatenated the way the host
    // really emits them: "#< CLIXML" appears ONCE, then one document per stream flush.
    private const string ProgressBody =
        Ns +
        "<Obj S=\"progress\" RefId=\"0\"><TN RefId=\"0\"><T>System.Management.Automation.PSCustomObject</T>" +
        "<T>System.Object</T></TN><MS><I64 N=\"SourceId\">1</I64><PR N=\"Record\">" +
        "<AV>Preparing modules for first use.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC>" +
        "<T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj></Objs>";

    private const string WarningBody =
        Ns + "<S S=\"Warning\">careful_x000D__x000A_</S></Objs>";

    // Three <S S="Error"> records, but the last is whitespace only — a real Write-Error trailer.
    // It decodes to two error LINES, which is what ExtractErrors has always reported.
    private const string ErrorBody =
        Ns +
        "<S S=\"Error\">Write-Error 'boom'; 'after' : boom_x000D__x000A_</S>" +
        "<S S=\"Error\">    + CategoryInfo          : NotSpecified: (:) [Write-Error], WriteErrorException_x000D__x000A_</S>" +
        "<S S=\"Error\"> _x000D__x000A_</S></Objs>";

    private const string ProgressOnly = Header + ProgressBody;
    private const string WarningOnly = Header + WarningBody;
    private const string ErrorOnly = Header + ErrorBody;

    // The common real-world shape: a first-use progress record, then something worth reading.
    // This is why the parser wraps the payload in a synthetic root before parsing.
    private const string ProgressThenWarningThenError = Header + ProgressBody + WarningBody + ErrorBody;

    [Fact]
    public void Progress_records_are_dropped_entirely()
    {
        // The whole point of D-8: a realistic blob of XML becomes nothing.
        ClixmlStderr.Decode(ProgressOnly).Should().BeEmpty();
        ProgressOnly.Length.Should().BeGreaterThan(300, "the sample must be a realistic blob");
    }

    [Fact]
    public void Warning_records_become_prefixed_text()
    {
        var decoded = ClixmlStderr.Decode(WarningOnly);

        decoded.Should().Be("WARNING: careful\n");
        decoded.Should().NotContain("<Objs").And.NotContain("_x000D_");
    }

    [Fact]
    public void Error_records_are_kept_as_text_with_escapes_decoded()
    {
        var decoded = ClixmlStderr.Decode(ErrorOnly);

        decoded.Should().StartWith("ERROR: ").And.Contain("boom");
        decoded.Should().NotContain("_x000D_", "CLIXML escapes must be decoded");
        decoded.Should().NotContain("<S ");
    }

    [Fact]
    public void Mixed_streams_keep_the_readable_ones_and_drop_progress()
    {
        var decoded = ClixmlStderr.Decode(ProgressThenWarningThenError);

        decoded.Should().Contain("WARNING: careful");
        decoded.Should().Contain("ERROR: ").And.Contain("boom");
        decoded.Should().NotContain("Preparing modules for first use.");
    }

    [Fact]
    public void Concatenated_documents_are_all_parsed()
    {
        ClixmlStderr.TryParseRecords(ProgressThenWarningThenError, out var records).Should().BeTrue();
        records.Should().Contain(r => r.Stream == "Warning");
        records.Should().Contain(r => r.Stream == "Error");
    }

    // Losing output is worse than a big blob: anything we cannot confidently decode passes through.
    [Fact]
    public void Non_clixml_stderr_passes_through_untouched()
    {
        const string raw = "git: 'frobnicate' is not a git command.\nSee 'git --help'.\n";
        ClixmlStderr.Decode(raw).Should().Be(raw);
    }

    [Fact]
    public void Unparseable_clixml_passes_through_untouched()
    {
        const string broken = Header + Ns + "<S S=\"Error\">truncated";
        ClixmlStderr.Decode(broken).Should().Be(broken);
    }

    // D-9: a background job's stderr is read incrementally, so a read lands mid-flush. Everything
    // up to the last complete </Objs> is decoded and the trailing fragment is dropped — it arrives
    // whole on the next read, and it is XML nobody could have read anyway.
    [Fact]
    public void A_trailing_partial_document_does_not_lose_the_complete_ones()
    {
        const string midFlush = Header + WarningBody + Ns + "<S S=\"Error\">half-writt";

        var decoded = ClixmlStderr.Decode(midFlush);

        decoded.Should().Be("WARNING: careful\n");
        decoded.Should().NotContain("half-writt", "an incomplete record is dropped, not emitted raw");
    }

    // The tolerance must not turn genuinely non-CLIXML content into a silent data loss: with no
    // complete document at all there is nothing to salvage, so the raw stream is returned.
    [Fact]
    public void A_stream_with_no_complete_document_stays_raw()
    {
        const string noneComplete = Header + Ns + "<S S=\"Error\">half-writt";
        ClixmlStderr.Decode(noneComplete).Should().Be(noneComplete);
    }

    [Fact]
    public void Empty_and_header_only_stderr_decode_to_empty()
    {
        ClixmlStderr.Decode("").Should().BeEmpty();
        ClixmlStderr.Decode(Header).Should().BeEmpty();
    }

    // The decoder and ExtractErrors share one parser; Errors[] semantics must not have moved.
    [Theory]
    [InlineData(ProgressOnly, 0)]
    [InlineData(WarningOnly, 0)]
    [InlineData(ErrorOnly, 2)]
    [InlineData(ProgressThenWarningThenError, 2)]
    public void ExtractErrors_is_unchanged_by_the_shared_parser(string stderr, int expectedErrors)
    {
        PowerShellService.ExtractErrors(stderr).Should().HaveCount(expectedErrors);
    }
}

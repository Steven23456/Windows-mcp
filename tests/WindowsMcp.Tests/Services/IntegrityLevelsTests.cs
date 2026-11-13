using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// E-8: <c>IntegrityLevels.Name(rid)</c> - the mandatory-label RID as SessionState.IntegrityLevel.
/// The design note's table: &lt; 0x1000 Untrusted, &lt; 0x2000 Low, &lt; 0x2100 Medium, &lt; 0x3000
/// MediumPlus, &lt; 0x4000 High, &lt; 0x5000 System, else Protected. Every boundary from both sides.
/// </summary>
/// <remarks>
/// The RID is a DWORD, so the stub takes <c>uint</c>: a "negative" RID is not representable, and the
/// high-bit values it would reinterpret as (0x80000000, 0xFFFFFFFF) fall under "else Protected"
/// exactly as the note's table says. "Unknown" is reserved for a RID that could not be READ - the
/// service's job, not this mapping's - so no RID maps to it.
/// </remarks>
[Trait("Category", "Unit")]
public class IntegrityLevelsTests
{
    [Theory]
    [InlineData(0x0000u, "Untrusted")]
    [InlineData(0x0FFFu, "Untrusted")]
    [InlineData(0x1000u, "Low")]
    [InlineData(0x1FFFu, "Low")]
    [InlineData(0x2000u, "Medium")]
    [InlineData(0x20FFu, "Medium")]
    [InlineData(0x2100u, "MediumPlus")]
    [InlineData(0x2FFFu, "MediumPlus")]
    [InlineData(0x3000u, "High")]
    [InlineData(0x3FFFu, "High")]
    [InlineData(0x4000u, "System")]
    [InlineData(0x4FFFu, "System")]
    [InlineData(0x5000u, "Protected")]
    [InlineData(0x5001u, "Protected")]
    [InlineData(0x7FFFFFFFu, "Protected")]
    [InlineData(0x80000000u, "Protected")]
    [InlineData(0xFFFFFFFFu, "Protected")]
    public void Name_maps_every_boundary_of_the_design_table(uint rid, string expected)
        => IntegrityLevels.Name(rid).Should().Be(expected);

    [Theory]
    [InlineData(0x1000u)]   // SECURITY_MANDATORY_LOW_RID
    [InlineData(0x2000u)]   // SECURITY_MANDATORY_MEDIUM_RID
    [InlineData(0x2100u)]   // SECURITY_MANDATORY_MEDIUM_PLUS_RID
    [InlineData(0x3000u)]   // SECURITY_MANDATORY_HIGH_RID
    [InlineData(0x4000u)]   // SECURITY_MANDATORY_SYSTEM_RID
    [InlineData(0x5000u)]   // SECURITY_MANDATORY_PROTECTED_PROCESS_RID
    public void Name_never_says_Unknown_for_a_rid_that_was_read(uint rid)
        => IntegrityLevels.Name(rid).Should().NotBe("Unknown");
}

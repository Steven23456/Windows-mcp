using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-12 phase 1 (R1, R2): the pure core. Everything the virtual-desktop inventory decides —
/// order, index, id format, the name fallback, which desktop is current, what a malformed blob
/// means — is decided here, on byte arrays, with no registry and no COM in the room. The
/// registry-shaped wiring is pinned separately in <see cref="VirtualDesktopServiceTests"/> and
/// the real key layout in <c>VirtualDesktopServiceIntegrationTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class VirtualDesktopRegistryTests
{
    private static readonly Guid G1 = new("3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa");
    private static readonly Guid G2 = new("96a9d868-feea-4270-bf42-ffcfae7316f5");
    private static readonly Guid G3 = new("cd3cdef5-1984-4578-ba3a-43116b2ab7ef");

    /// <summary>The registry blob: one GUID after another, 16 bytes each, no header.</summary>
    private static byte[] Blob(params Guid[] guids)
        => guids.SelectMany(g => g.ToByteArray()).ToArray();

    private static Func<Guid, string?> Names(params (Guid Guid, string? Name)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Guid, p => p.Name);
        return g => map.TryGetValue(g, out var n) ? n : null;
    }

    private static readonly Func<Guid, string?> NoNames = _ => null;

    // ---- R2: ids, order, index ---------------------------------------------------------------

    [Fact]
    public void Parse_returns_one_entry_per_16_byte_guid_in_registry_order()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2, G3), current: null, NoNames);

        desktops.Select(d => d.Id).Should().Equal(
            "3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa",
            "96a9d868-feea-4270-bf42-ffcfae7316f5",
            "cd3cdef5-1984-4578-ba3a-43116b2ab7ef");
        desktops.Select(d => d.Index).Should().Equal(new[] { 0, 1, 2 },
            "Index is the zero-based position in VirtualDesktopIDs, which is the user's left-to-right order");
    }

    [Fact]
    public void Parse_reads_the_guid_bytes_the_way_the_registry_wrote_them()
    {
        // The pin that catches a big-endian reader: these 16 bytes are exactly what Windows
        // stores for {3B3C1D2E-4F50-6172-8394-A5B6C7D8E9FA} (Data1/2/3 little-endian, the rest
        // in order) — i.e. `new Guid(bytes)`, not a byte-order-swapped reading of them.
        byte[] bytes =
        [
            0x2e, 0x1d, 0x3c, 0x3b, 0x50, 0x4f, 0x72, 0x61,
            0x83, 0x94, 0xa5, 0xb6, 0xc7, 0xd8, 0xe9, 0xfa
        ];

        var desktops = VirtualDesktopRegistry.Parse(bytes, current: null, NoNames);

        desktops.Should().ContainSingle().Which.Id.Should().Be("3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa");
    }

    [Fact]
    public void Parse_writes_the_id_lower_case_dashed_with_no_braces()
    {
        var id = VirtualDesktopRegistry.Parse(Blob(G2), current: null, NoNames).Single().Id;

        id.Should().Be(G2.ToString("D"), "the 'D' format is what WindowInfo.DesktopId carries too");
        id.Should().MatchRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        id.Should().NotContain("{").And.NotContain("}").And.NotBe(id.ToUpperInvariant());
    }

    // ---- R2: names -------------------------------------------------------------------------

    [Fact]
    public void Parse_uses_the_stored_name_of_each_desktop()
    {
        var desktops = VirtualDesktopRegistry.Parse(
            Blob(G1, G2), current: null, Names((G1, "Work"), (G2, "Play")));

        desktops.Select(d => d.Name).Should().Equal("Work", "Play");
    }

    [Fact]
    public void Parse_asks_for_the_name_of_every_desktop_exactly_once()
    {
        var asked = new List<Guid>();
        string? Record(Guid g) { asked.Add(g); return null; }

        VirtualDesktopRegistry.Parse(Blob(G1, G2, G3), current: null, Record);

        asked.Should().Equal(new[] { G1, G2, G3 },
            "one name lookup per desktop, in list order — no extras, no misses");
    }

    [Fact]
    public void Parse_falls_back_to_Desktop_N_when_no_name_is_stored()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2, G3), current: null, Names((G2, "Play")));

        desktops.Select(d => d.Name).Should().Equal(new[] { "Desktop 1", "Play", "Desktop 3" },
            "the fallback is one-based (Desktop 1 is Index 0), which is what the Task View shows");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Parse_treats_a_blank_stored_name_as_no_name(string stored)
    {
        // Windows leaves an empty REG_SZ 'Name' behind for a desktop the user never renamed
        // (observed on 10.0.28000: three Desktops subkeys, every Name empty).
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1), current: null, Names((G1, stored)));

        desktops.Single().Name.Should().Be("Desktop 1");
    }

    [Fact]
    public void Parse_keeps_a_padded_name_verbatim()
    {
        // Resolved ambiguity: "blank counts as missing" is IsNullOrWhiteSpace; a name with
        // content is the user's, spaces and all, and is not trimmed or sanitised here.
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1), current: null, Names((G1, " Work ")));

        desktops.Single().Name.Should().Be(" Work ");
    }

    [Fact]
    public void Parse_keeps_a_unicode_name_verbatim()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1), current: null, Names((G1, "作業 · déjà vu")));

        desktops.Single().Name.Should().Be("作業 · déjà vu");
    }

    // ---- R2: which one is current ------------------------------------------------------------

    [Fact]
    public void Parse_marks_exactly_the_desktop_the_current_blob_names()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2, G3), Blob(G2), NoNames);

        desktops.Select(d => d.IsCurrent).Should().Equal(false, true, false);
        desktops.Count(d => d.IsCurrent).Should().Be(1, "exactly one desktop is current");
    }

    [Fact]
    public void Parse_marks_none_current_when_there_is_no_current_blob()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2), current: null, NoNames);

        desktops.Should().OnlyContain(d => !d.IsCurrent,
            "an absent CurrentVirtualDesktop value means 'unknown', not 'the first one'");
        desktops.Should().HaveCount(2, "the list still comes back — only the flag is unknown");
    }

    [Fact]
    public void Parse_marks_none_current_when_the_current_guid_is_not_in_the_list()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2), Blob(G3), NoNames);

        desktops.Should().OnlyContain(d => !d.IsCurrent);
    }

    [Fact]
    public void Parse_marks_none_current_when_the_current_blob_is_not_16_bytes()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2), current: [1, 2, 3, 4], NoNames);

        desktops.Should().HaveCount(2).And.OnlyContain(d => !d.IsCurrent,
            "a short blob is unreadable, not a reason to throw");
    }

    [Fact]
    public void Parse_marks_none_current_when_the_current_blob_is_empty()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1), current: [], NoNames);

        desktops.Should().ContainSingle().Which.IsCurrent.Should().BeFalse();
    }

    [Fact]
    public void Parse_reads_only_the_first_16_bytes_of_an_over_long_current_blob()
    {
        var over = Blob(G2).Concat(new byte[] { 0xff, 0xee }).ToArray();

        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2), over, NoNames);

        desktops.Select(d => d.IsCurrent).Should().Equal(false, true);
    }

    // ---- R2: nothing, and malformed ----------------------------------------------------------

    [Fact]
    public void Parse_returns_an_empty_array_when_the_ids_blob_is_null()
    {
        VirtualDesktopRegistry.Parse(null, Blob(G1), NoNames)
            .Should().BeEmpty("a missing VirtualDesktopIDs value means 'no desktops listed', not an exception");
    }

    [Fact]
    public void Parse_returns_an_empty_array_when_the_ids_blob_is_empty()
    {
        VirtualDesktopRegistry.Parse([], null, NoNames).Should().BeEmpty();
    }

    [Fact]
    public void Parse_ignores_a_trailing_partial_guid()
    {
        var ragged = Blob(G1, G2).Concat(new byte[] { 1, 2, 3, 4, 5, 6, 7 }).ToArray();

        var desktops = VirtualDesktopRegistry.Parse(ragged, current: null, NoNames);

        desktops.Select(d => d.Id).Should().Equal(new[] { G1.ToString("D"), G2.ToString("D") },
            "7 bytes are not a GUID; the complete ones before them still count");
    }

    [Fact]
    public void Parse_returns_an_empty_array_when_the_ids_blob_is_shorter_than_one_guid()
    {
        VirtualDesktopRegistry.Parse([9, 9, 9], null, NoNames).Should().BeEmpty();
    }

    [Fact]
    public void Parse_never_returns_null()
    {
        VirtualDesktopRegistry.Parse(null, null, NoNames).Should().NotBeNull();
    }

    [Fact]
    public void Parse_carries_every_field_of_a_named_current_desktop()
    {
        var desktops = VirtualDesktopRegistry.Parse(Blob(G1, G2), Blob(G2), Names((G2, "Play")));

        desktops[1].Should().Be(new VirtualDesktopInfo(
            Id: "96a9d868-feea-4270-bf42-ffcfae7316f5",
            Name: "Play",
            Index: 1,
            IsCurrent: true));
    }

    // ---- R2: GuidKey -------------------------------------------------------------------------

    [Fact]
    public void GuidKey_is_the_guid_in_upper_case_braces()
    {
        VirtualDesktopRegistry.GuidKey(G1).Should().Be("{3B3C1D2E-4F50-6172-8394-A5B6C7D8E9FA}",
            "that is the literal subkey name under …\\VirtualDesktops\\Desktops");
    }

    [Fact]
    public void GuidKey_matches_the_subkey_names_windows_actually_writes()
    {
        // Observed under HKCU\…\Explorer\VirtualDesktops\Desktops on 10.0.28000.
        VirtualDesktopRegistry.GuidKey(G2).Should().Be("{96A9D868-FEEA-4270-BF42-FFCFAE7316F5}");
        VirtualDesktopRegistry.GuidKey(G3).Should().Be("{CD3CDEF5-1984-4578-BA3A-43116B2AB7EF}");
    }

    [Fact]
    public void GuidKey_round_trips_the_guid()
    {
        Guid.Parse(VirtualDesktopRegistry.GuidKey(G1)).Should().Be(G1);
    }

    [Fact]
    public void GuidKey_of_the_empty_guid_is_still_braced()
    {
        VirtualDesktopRegistry.GuidKey(Guid.Empty).Should().Be("{00000000-0000-0000-0000-000000000000}");
    }
}

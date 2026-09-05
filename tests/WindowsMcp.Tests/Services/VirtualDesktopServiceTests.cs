using System.Diagnostics;
using FluentAssertions;
using Moq;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// A-12 phase 1 (R3): the registry half of <see cref="VirtualDesktopService"/>, driven by a
/// <see cref="Mock{IRegistryService}"/> that mimics the <b>real</b> RegistryService's failure
/// modes — <see cref="KeyNotFoundException"/> for a missing key (RegistryService.GetAsync throws
/// it explicitly) and <see cref="IOException"/> for a missing value under an existing key
/// (RegistryKey.GetValueKind throws "The specified registry key does not exist."). Both are
/// normal on a real box: on 10.0.28000 the VirtualDesktops key exists with no VirtualDesktopIDs
/// value at all. The COM half has no mock — it is pinned in
/// <c>VirtualDesktopServiceIntegrationTests</c>, and the whole registry path is re-run against
/// the real RegistryService there, because a hand-fed mock is not evidence (CLAUDE.md,
/// disk_inspect mode:reclaimable).
/// </summary>
[Trait("Category", "Unit")]
public class VirtualDesktopServiceTests
{
    private const string VdPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const string DesktopsPath = VdPath + @"\Desktops";
    private static string DesktopKey(Guid g) => $@"{DesktopsPath}\{g.ToString("B").ToUpperInvariant()}";

    /// <summary>
    /// Where Windows 11 keeps <c>CurrentVirtualDesktop</c> when the VirtualDesktops key does not:
    /// under this process's own session, which is the only session whose desktop is "current".
    /// </summary>
    private static readonly string SessionVdPath = SessionPath();

    private static string SessionPath()
    {
        using var self = Process.GetCurrentProcess();
        return $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{self.SessionId}\VirtualDesktops";
    }

    private static readonly Guid G1 = new("3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa");
    // Synthetic on purpose: with no CurrentVirtualDesktop value the service falls back to the
    // desktop the foreground window is on (COM), so a fixture that reused one of THIS machine's
    // real desktop ids would see it flagged. Made-up ids can never match.
    private static readonly Guid G2 = new("7c9e2b41-1d3a-4f6e-9a0b-2c5d8e1f3a47");

    private static byte[] Blob(params Guid[] guids) => guids.SelectMany(g => g.ToByteArray()).ToArray();

    /// <summary>
    /// A registry that has the VirtualDesktops key with the given values; every value that is
    /// not set up is "missing" the way the real service reports it.
    /// </summary>
    private static Mock<IRegistryService> Registry(object? ids, object? current, params (Guid Guid, object? Name)[] names)
    {
        var mock = new Mock<IRegistryService>();
        // Catch-all first: any value we do not set up is absent (IOException), which is what the
        // real RegistryService does for an existing key with no such value.
        mock.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The specified registry key does not exist."));
        if (ids is not null)
            mock.Setup(r => r.GetAsync("HKCU", VdPath, "VirtualDesktopIDs", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RegistryValueDto(VdPath, "VirtualDesktopIDs", ids, "Binary"));
        if (current is not null)
            mock.Setup(r => r.GetAsync("HKCU", VdPath, "CurrentVirtualDesktop", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RegistryValueDto(VdPath, "CurrentVirtualDesktop", current, "Binary"));
        foreach (var (guid, name) in names)
            mock.Setup(r => r.GetAsync("HKCU", DesktopKey(guid), "Name", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RegistryValueDto(DesktopKey(guid), "Name", name, "String"));
        return mock;
    }

    /// <summary>The Desktops key with these sub-key names, in the order the registry hands them over.</summary>
    private static void SubKeys(Mock<IRegistryService> registry, params string[] names)
        => registry.Setup(r => r.EnumerateSubKeysAsync("HKCU", DesktopsPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(names);

    // ---- R3: ListAsync reads the documented location -----------------------------------------

    [Fact]
    public async Task ListAsync_reads_the_two_values_of_the_documented_virtual_desktops_key()
    {
        var registry = Registry(Blob(G1, G2), Blob(G2), (G1, "Work"), (G2, "Play"));

        await new VirtualDesktopService(registry.Object).ListAsync();

        registry.Verify(r => r.GetAsync("HKCU", VdPath, "VirtualDesktopIDs", It.IsAny<CancellationToken>()),
            Times.Once, "the desktop order comes from one REG_BINARY value, read once");
        registry.Verify(r => r.GetAsync("HKCU", VdPath, "CurrentVirtualDesktop", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListAsync_reads_each_name_from_its_braced_guid_subkey()
    {
        var registry = Registry(Blob(G1, G2), null, (G1, "Work"), (G2, "Play"));

        await new VirtualDesktopService(registry.Object).ListAsync();

        registry.Verify(r => r.GetAsync(
                "HKCU",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{3B3C1D2E-4F50-6172-8394-A5B6C7D8E9FA}",
                "Name", It.IsAny<CancellationToken>()),
            Times.Once, "upper-case braces is how Windows names the subkey");
        registry.Verify(r => r.GetAsync(
                "HKCU",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{7C9E2B41-1D3A-4F6E-9A0B-2C5D8E1F3A47}",
                "Name", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ListAsync_maps_the_registry_onto_the_inventory()
    {
        var registry = Registry(Blob(G1, G2), Blob(G2), (G1, "Work"), (G2, "Play"));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().Equal(
            new VirtualDesktopInfo("3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa", "Work", 0, false),
            new VirtualDesktopInfo("7c9e2b41-1d3a-4f6e-9a0b-2c5d8e1f3a47", "Play", 1, true));
    }

    [Fact]
    public async Task ListAsync_forwards_the_cancellation_token_to_the_registry()
    {
        using var cts = new CancellationTokenSource();
        var registry = Registry(Blob(G1), null, (G1, "Work"));

        await new VirtualDesktopService(registry.Object).ListAsync(cts.Token);

        registry.Verify(r => r.GetAsync("HKCU", VdPath, "VirtualDesktopIDs", cts.Token), Times.Once,
            "the caller's token has to reach the registry read, not be swallowed on the way");
    }

    // ---- R3: a registry that says nothing -----------------------------------------------------

    [Theory]
    [InlineData(typeof(KeyNotFoundException))]   // the key itself is absent (RegistryService.GetAsync)
    [InlineData(typeof(IOException))]            // the key is there, the value is not (GetValueKind)
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task ListAsync_is_empty_when_the_ids_value_cannot_be_read(Type exceptionType)
    {
        var registry = new Mock<IRegistryService>();
        registry.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType)!);

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().BeEmpty("a registry that will not answer means 'no desktops known', never a thrown tool call");
    }

    [Fact]
    public async Task ListAsync_is_empty_when_the_ids_value_holds_no_data()
    {
        var registry = Registry(ids: null, current: null);
        registry.Setup(r => r.GetAsync("HKCU", VdPath, "VirtualDesktopIDs", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryValueDto(VdPath, "VirtualDesktopIDs", null, "Binary"));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_is_empty_when_the_ids_value_is_not_a_binary_blob()
    {
        // A REG_SZ where a REG_BINARY belongs: unreadable, not a crash.
        var registry = Registry(ids: "not a blob", current: null);

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_lists_the_desktops_even_when_the_current_value_is_missing()
    {
        // The observed 10.0.28000 shape: CurrentVirtualDesktop has moved out of this key.
        var registry = Registry(Blob(G1, G2), current: null, (G1, "Work"), (G2, "Play"));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().HaveCount(2).And.OnlyContain(d => !d.IsCurrent);
    }

    [Fact]
    public async Task ListAsync_ignores_a_current_value_that_is_not_a_binary_blob()
    {
        var registry = Registry(Blob(G1), current: "nonsense");

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().ContainSingle().Which.IsCurrent.Should().BeFalse();
    }

    // ---- R3: names that are not there ---------------------------------------------------------

    [Fact]
    public async Task ListAsync_falls_back_to_Desktop_N_when_the_name_subkey_is_missing()
    {
        var registry = Registry(Blob(G1, G2), null, (G2, "Play"));
        registry.Setup(r => r.GetAsync("HKCU", DesktopKey(G1), "Name", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException($"Registry path not found: HKCU\\{DesktopKey(G1)}"));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Select(d => d.Name).Should().Equal(new[] { "Desktop 1", "Play" },
            "one unreadable name does not cost the other desktop its name, or the list its entry");
    }

    [Fact]
    public async Task ListAsync_falls_back_when_the_name_value_holds_no_string()
    {
        var registry = Registry(Blob(G1), null, (G1, new byte[] { 1, 2, 3 }));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Single().Name.Should().Be("Desktop 1");
    }

    [Fact]
    public async Task ListAsync_falls_back_when_the_name_is_blank()
    {
        var registry = Registry(Blob(G1), null, (G1, "   "));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Single().Name.Should().Be("Desktop 1");
    }

    [Fact]
    public async Task ListAsync_does_not_throw_when_reading_a_name_is_denied()
    {
        var registry = Registry(Blob(G1), null);
        registry.Setup(r => r.GetAsync("HKCU", DesktopKey(G1), "Name", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());

        var act = () => new VirtualDesktopService(registry.Object).ListAsync();

        await act.Should().NotThrowAsync();
        (await act()).Single().Name.Should().Be("Desktop 1");
    }

    // ---- R3: GetCurrentAsync ------------------------------------------------------------------

    [Fact]
    public async Task GetCurrentAsync_returns_the_flagged_entry_of_the_list()
    {
        var registry = Registry(Blob(G1, G2), Blob(G2), (G1, "Work"), (G2, "Play"));

        var current = await new VirtualDesktopService(registry.Object).GetCurrentAsync();

        current.Should().Be(new VirtualDesktopInfo("7c9e2b41-1d3a-4f6e-9a0b-2c5d8e1f3a47", "Play", 1, true));
    }

    [Fact]
    public async Task GetCurrentAsync_is_null_when_no_desktop_is_flagged()
    {
        var registry = Registry(Blob(G1, G2), current: null, (G1, "Work"));

        var current = await new VirtualDesktopService(registry.Object).GetCurrentAsync();

        current.Should().BeNull("with no CurrentVirtualDesktop value and no listed desktop matching the foreground window's, there is no current desktop to name");
    }

    [Fact]
    public async Task GetCurrentAsync_is_null_when_there_are_no_desktops_at_all()
    {
        var registry = new Mock<IRegistryService>();
        registry.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException());

        var act = () => new VirtualDesktopService(registry.Object).GetCurrentAsync();

        await act.Should().NotThrowAsync();
        (await act()).Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentAsync_forwards_the_cancellation_token()
    {
        using var cts = new CancellationTokenSource();
        var registry = Registry(Blob(G1), Blob(G1), (G1, "Work"));

        await new VirtualDesktopService(registry.Object).GetCurrentAsync(cts.Token);

        registry.Verify(r => r.GetAsync("HKCU", VdPath, "VirtualDesktopIDs", cts.Token), Times.Once);
    }

    // ---- R3 (fallback a): the ids come from the Desktops sub-keys when the blob is absent -----
    // Observed on 10.0.28000: the VirtualDesktops key has no VirtualDesktopIDs value at all, only
    // one sub-key per desktop. Without this fallback every desktop-aware answer is empty there.

    [Fact]
    public async Task ListAsync_falls_back_to_the_desktop_subkeys_when_the_ids_blob_is_absent()
    {
        var registry = Registry(ids: null, current: null, (G2, "Play"), (G1, "Work"));
        SubKeys(registry, "{7C9E2B41-1D3A-4F6E-9A0B-2C5D8E1F3A47}", "{3B3C1D2E-4F50-6172-8394-A5B6C7D8E9FA}");

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Select(d => d.Id).Should().Equal(
            new[] { "7c9e2b41-1d3a-4f6e-9a0b-2c5d8e1f3a47", "3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa" },
            "the sub-key enumeration order is the desktop order - it is not re-sorted");
        desktops.Select(d => d.Index).Should().Equal(new[] { 0, 1 });
        desktops.Select(d => d.Name).Should().Equal(new[] { "Play", "Work" },
            "a fallback desktop still gets its name from its own sub-key");
        registry.Verify(r => r.EnumerateSubKeysAsync("HKCU", DesktopsPath, It.IsAny<CancellationToken>()),
            Times.Once, "one enumeration of the documented Desktops key, no other path");
    }

    [Fact]
    public async Task ListAsync_falls_back_to_the_desktop_subkeys_when_the_ids_blob_is_shorter_than_one_guid()
    {
        var registry = Registry(ids: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, current: null, (G1, "Work"));
        SubKeys(registry, "{3B3C1D2E-4F50-6172-8394-A5B6C7D8E9FA}");

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().ContainSingle("half a GUID is as unusable as no value at all")
            .Which.Name.Should().Be("Work");
    }

    [Fact]
    public async Task ListAsync_skips_a_subkey_that_is_not_a_braced_guid()
    {
        // The Desktops key is not guaranteed to hold only desktops; anything unparseable is not one.
        var registry = Registry(ids: null, current: null);
        SubKeys(registry,
            "{3B3C1D2E-4F50-6172-8394-A5B6C7D8E9FA}",
            "junk",
            "3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa",   // unbraced: not the shape Windows writes
            "{7C9E2B41-1D3A-4F6E-9A0B-2C5D8E1F3A47}");

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Select(d => d.Id).Should().Equal(
            new[] { "3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa", "7c9e2b41-1d3a-4f6e-9a0b-2c5d8e1f3a47" });
        desktops.Select(d => d.Index).Should().Equal(new[] { 0, 1 },
            "the index counts desktops, not skipped sub-keys");
    }

    [Fact]
    public async Task ListAsync_does_not_enumerate_the_subkeys_when_the_ids_blob_is_usable()
    {
        var registry = Registry(Blob(G1, G2), current: null, (G1, "Work"), (G2, "Play"));
        SubKeys(registry, "{DEADBEEF-0000-0000-0000-000000000000}");

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Select(d => d.Id).Should().Equal(
            new[] { "3b3c1d2e-4f50-6172-8394-a5b6c7d8e9fa", "7c9e2b41-1d3a-4f6e-9a0b-2c5d8e1f3a47" },
            "the ordered blob is the authority where it exists; the sub-keys are only its stand-in");
        registry.Verify(r => r.EnumerateSubKeysAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- R3 (fallback b): a sub-key enumeration that says nothing usable ----------------------

    [Fact]
    public async Task ListAsync_is_empty_when_the_subkey_enumeration_throws()
    {
        var registry = Registry(ids: null, current: null);
        registry.Setup(r => r.EnumerateSubKeysAsync("HKCU", DesktopsPath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());

        var act = () => new VirtualDesktopService(registry.Object).ListAsync();

        await act.Should().NotThrowAsync("a Desktops key that will not enumerate is 'no desktops known'");
        (await act()).Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_is_empty_when_the_subkey_enumeration_returns_null()
    {
        var registry = Registry(ids: null, current: null);
        registry.Setup(r => r.EnumerateSubKeysAsync("HKCU", DesktopsPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string[])null!);

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().BeEmpty("a null array is 'nothing', not a NullReferenceException on the way out");
    }

    [Fact]
    public async Task ListAsync_is_empty_when_the_subkey_enumeration_is_empty()
    {
        var registry = Registry(ids: null, current: null);
        SubKeys(registry);

        (await new VirtualDesktopService(registry.Object).ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_is_empty_when_no_subkey_is_a_braced_guid()
    {
        var registry = Registry(ids: null, current: null);
        SubKeys(registry, "junk", "MRU", "");

        (await new VirtualDesktopService(registry.Object).ListAsync()).Should().BeEmpty(
            "sub-keys that are not desktops do not add up to a desktop");
    }

    // ---- R3 (fallback c): the current desktop under SessionInfo -------------------------------

    [Fact]
    public async Task ListAsync_flags_the_current_desktop_from_the_session_info_key_when_the_main_key_has_none()
    {
        // Windows 11 moved CurrentVirtualDesktop into the per-session key; the desktop list did
        // not move with it, so both keys have to be read for one answer.
        var registry = Registry(Blob(G1, G2), current: null, (G1, "Work"), (G2, "Play"));
        registry.Setup(r => r.GetAsync("HKCU", SessionVdPath, "CurrentVirtualDesktop", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryValueDto(SessionVdPath, "CurrentVirtualDesktop", Blob(G2), "Binary"));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Select(d => d.IsCurrent).Should().Equal(new[] { false, true },
            "the session key is where this build records the current desktop: " + SessionVdPath);
        registry.Verify(r => r.GetAsync("HKCU", SessionVdPath, "CurrentVirtualDesktop", It.IsAny<CancellationToken>()),
            Times.Once, "read once, from this process's own session - not session 1, not every session");
    }

    [Fact]
    public async Task ListAsync_prefers_the_virtual_desktops_key_over_the_session_info_key()
    {
        var registry = Registry(Blob(G1, G2), Blob(G1), (G1, "Work"), (G2, "Play"));
        registry.Setup(r => r.GetAsync("HKCU", SessionVdPath, "CurrentVirtualDesktop", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistryValueDto(SessionVdPath, "CurrentVirtualDesktop", Blob(G2), "Binary"));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Select(d => d.IsCurrent).Should().Equal(new[] { true, false },
            "the documented location wins; the session key is the fallback, not an override");
        registry.Verify(r => r.GetAsync("HKCU", SessionVdPath, It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "a value that was already found is not looked for a second time");
    }

    // ---- R3 (fallback d): nothing in the registry names the current desktop -------------------

    [Fact]
    public async Task ListAsync_flags_nothing_when_neither_registry_location_names_the_current_desktop()
    {
        // The last resort is COM (the desktop the foreground window is on), which cannot be mocked
        // here - but it can never invent a match: these two ids are made up, so whatever this
        // machine's real current desktop is, no entry of this list is it.
        var registry = Registry(Blob(G1, G2), current: null, (G1, "Work"), (G2, "Play"));

        var desktops = await new VirtualDesktopService(registry.Object).ListAsync();

        desktops.Should().HaveCount(2).And.OnlyContain(d => !d.IsCurrent,
            "an unknown current desktop leaves every entry unflagged - it never defaults to the first");
        registry.Verify(r => r.GetAsync("HKCU", SessionVdPath, "CurrentVirtualDesktop", It.IsAny<CancellationToken>()),
            Times.Once, "the session key is tried before the service falls through to the COM query");
    }

    // ---- R3: cancellation is not 'no data' ----------------------------------------------------

    [Fact]
    public async Task ListAsync_throws_before_reading_anything_when_the_token_is_already_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var registry = Registry(Blob(G1), Blob(G1), (G1, "Work"));

        var act = () => new VirtualDesktopService(registry.Object).ListAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        registry.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the guard comes first: a cancelled call touches no registry key at all");
    }

    [Fact]
    public async Task ListAsync_propagates_a_cancellation_raised_by_the_ids_read()
    {
        var registry = Registry(ids: null, current: null);
        registry.Setup(r => r.GetAsync("HKCU", VdPath, "VirtualDesktopIDs", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => new VirtualDesktopService(registry.Object).ListAsync();

        await act.Should().ThrowAsync<OperationCanceledException>(
            "a cancelled read is the caller giving up, not a registry that has no desktops");
    }

    [Fact]
    public async Task ListAsync_propagates_a_cancellation_raised_by_a_name_read()
    {
        var registry = Registry(Blob(G1), current: null);
        registry.Setup(r => r.GetAsync("HKCU", DesktopKey(G1), "Name", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => new VirtualDesktopService(registry.Object).ListAsync();

        await act.Should().ThrowAsync<OperationCanceledException>(
            "'Desktop 1' is the fallback for a missing name, not for an abandoned call");
    }

    [Fact]
    public async Task ListAsync_propagates_a_cancellation_raised_by_the_subkey_enumeration()
    {
        var registry = Registry(ids: null, current: null);
        registry.Setup(r => r.EnumerateSubKeysAsync("HKCU", DesktopsPath, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => new VirtualDesktopService(registry.Object).ListAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetWindowDesktopIdAsync_throws_when_the_token_is_already_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = new VirtualDesktopService(Registry(ids: null, current: null).Object);

        var act = () => service.GetWindowDesktopIdAsync(0, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the cancellation guard runs before the hwnd-0 shortcut, so a cancelled call is never a quiet null");
    }

    [Fact]
    public async Task IsWindowOnCurrentDesktopAsync_throws_when_the_token_is_already_cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = new VirtualDesktopService(Registry(ids: null, current: null).Object);

        var act = () => service.IsWindowOnCurrentDesktopAsync(0, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

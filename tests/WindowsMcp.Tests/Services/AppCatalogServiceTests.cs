using FluentAssertions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// B-8 / roadmap C7: the caching half of the app catalog — when the two sources are read, when
/// they are read again, and what a resolve does when it misses. Driven through the constructor
/// seam with two counting fakes and a fake clock, so the five-minute TTL is pinned without
/// waiting five minutes and the refresh-on-miss is pinned without a Start Menu.
/// <para>
/// The rules themselves (merge, exact/prefix/fuzzy) are <see cref="AppCatalogTests"/>'s; the real
/// sources are <see cref="AppCatalogServiceIntegrationTests"/>'s — a mocked collaborator is not
/// evidence that the WinRT enumeration works (CLAUDE.md).
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class AppCatalogServiceTests
{
    /// <summary>Only <c>GetUtcNow</c> matters: the TTL is a comparison, not a timer.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>A source that counts how many times it was enumerated and can change its answer.</summary>
    private sealed class CountingSource
    {
        private readonly Func<int, IEnumerable<AppEntry>> _answers;
        public int Reads { get; private set; }
        public CountingSource(params AppEntry[] fixedAnswer) : this(_ => fixedAnswer) { }
        public CountingSource(Func<int, IEnumerable<AppEntry>> answers) => _answers = answers;
        public IEnumerable<AppEntry> Read()
        {
            Reads++;
            // Materialised: a lazily-enumerated source would let a caller inflate the count.
            return _answers(Reads).ToArray();
        }
    }

    private static AppEntry Shortcut(string name)
        => new(name, "shortcut", @"C:\ProgramData\Start Menu\" + name + ".lnk", @"C:\ProgramData\Start Menu");

    private static AppEntry Packaged(string name)
        => new(name, "packaged", "Contoso." + name + "_8wekyb3d8bbwe!App", "package:Contoso." + name + "_8wekyb3d8bbwe");

    private static AppCatalogService Service(CountingSource shortcuts, CountingSource packaged, TimeProvider clock)
        => new(shortcuts.Read, packaged.Read, clock);

    // ---- the sources are read, once, and merged ----------------------------------------------

    [Fact]
    public async Task ListAsync_merges_both_sources()
    {
        var shortcuts = new CountingSource(Shortcut("Notepad"));
        var packaged = new CountingSource(Packaged("Notepad"), Packaged("Calculator"));

        var list = await Service(shortcuts, packaged, new FakeClock()).ListAsync();

        list.Select(e => e.Name).Should().Equal("Calculator", "Notepad");
        list.Single(e => e.Name == "Notepad").Kind.Should().Be("shortcut",
            "the merge rule is AppCatalog.Merge's and the service must not invent its own");
        shortcuts.Reads.Should().Be(1);
        packaged.Reads.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_twice_inside_the_ttl_reads_the_sources_once()
    {
        // Enumerating a few hundred packages costs the best part of a second (roadmap risk list);
        // launch must not pay it twice in a conversation.
        var shortcuts = new CountingSource(Shortcut("Notepad"));
        var packaged = new CountingSource(Packaged("Calculator"));
        var clock = new FakeClock();
        var service = Service(shortcuts, packaged, clock);

        var first = await service.ListAsync();
        clock.Advance(TimeSpan.FromMinutes(4));
        var second = await service.ListAsync();

        second.Select(e => e.Name).Should().Equal(first.Select(e => e.Name));
        shortcuts.Reads.Should().Be(1);
        packaged.Reads.Should().Be(1, "the second call is served from the cache");
    }

    [Fact]
    public async Task ListAsync_at_exactly_five_minutes_is_still_fresh()
    {
        // "Older than five minutes" - five minutes on the nose is not older than five minutes.
        var shortcuts = new CountingSource(Shortcut("Notepad"));
        var packaged = new CountingSource();
        var clock = new FakeClock();
        var service = Service(shortcuts, packaged, clock);
        await service.ListAsync();

        clock.Advance(AppCatalogService.CacheTtl);
        await service.ListAsync();

        shortcuts.Reads.Should().Be(1);
    }

    [Fact]
    public async Task ListAsync_past_the_ttl_reads_the_sources_again()
    {
        var shortcuts = new CountingSource(read => read == 1 ? [Shortcut("Notepad")] : [Shortcut("Notepad"), Shortcut("Slack")]);
        var packaged = new CountingSource();
        var clock = new FakeClock();
        var service = Service(shortcuts, packaged, clock);
        await service.ListAsync();

        clock.Advance(AppCatalogService.CacheTtl + TimeSpan.FromMilliseconds(1));
        var second = await service.ListAsync();

        shortcuts.Reads.Should().Be(2, "a stale catalog would never see an app installed since");
        second.Select(e => e.Name).Should().Equal(new[] { "Notepad", "Slack" },
            "the refreshed list is what is returned, not the stale one");
    }

    [Fact]
    public void CacheTtl_is_five_minutes()
    {
        AppCatalogService.CacheTtl.Should().Be(TimeSpan.FromMinutes(5), "roadmap C7 sets it");
    }

    // ---- resolve: hit, miss, and the one refresh ---------------------------------------------

    [Fact]
    public async Task ResolveAsync_answers_from_the_cache_without_re_reading_the_sources()
    {
        var shortcuts = new CountingSource(Shortcut("Calculator"));
        var packaged = new CountingSource();
        var service = Service(shortcuts, packaged, new FakeClock());
        await service.ListAsync();

        var match = await service.ResolveAsync("calc");

        match.Entry.Name.Should().Be("Calculator");
        match.Strategy.Should().Be("prefix");
        shortcuts.Reads.Should().Be(1, "a hit is a hit: nothing is re-enumerated");
    }

    [Fact]
    public async Task ResolveAsync_reports_the_matcher_verdict_unchanged()
    {
        var shortcuts = new CountingSource(Shortcut("Visual Studio Code"));
        var service = Service(shortcuts, new CountingSource(), new FakeClock());

        var match = await service.ResolveAsync("vs code");

        match.Entry.Name.Should().Be("Visual Studio Code");
        match.Strategy.Should().Be("fuzzy");
        match.Score.Should().Be(73, "the score the shared FuzzyMatch gives, passed through");
    }

    [Fact]
    public async Task ResolveAsync_refreshes_once_when_it_misses_and_then_finds_the_new_app()
    {
        // The app the user just installed: the cache predates it, so a miss has to look again
        // before it gives up.
        var shortcuts = new CountingSource(read => read == 1 ? [] : [Shortcut("Calculator")]);
        var packaged = new CountingSource();
        var service = Service(shortcuts, packaged, new FakeClock());

        var match = await service.ResolveAsync("calc");

        match.Entry.Name.Should().Be("Calculator");
        shortcuts.Reads.Should().Be(2, "the cold read, then the refresh the miss triggered");
    }

    [Fact]
    public async Task ResolveAsync_refreshes_at_most_once_and_then_the_miss_stands()
    {
        var shortcuts = new CountingSource();
        var packaged = new CountingSource();
        var service = Service(shortcuts, packaged, new FakeClock());

        var act = () => service.ResolveAsync("calc");

        (await act.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("calc");
        shortcuts.Reads.Should().Be(2, "one refresh, not a retry loop");
        packaged.Reads.Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_does_not_refresh_a_second_time_for_a_second_miss_in_the_ttl()
    {
        // Two misses in a row must not cost four enumerations: after the first miss the catalog
        // is known to be fresh.
        var shortcuts = new CountingSource();
        var service = Service(shortcuts, new CountingSource(), new FakeClock());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ResolveAsync("calc"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ResolveAsync("edge"));

        shortcuts.Reads.Should().Be(2, "the refresh the first miss forced left a fresh catalog behind");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_refuses_a_blank_name_without_reading_anything(string name)
    {
        var shortcuts = new CountingSource(Shortcut("Calculator"));
        var service = Service(shortcuts, new CountingSource(), new FakeClock());

        var act = () => service.ResolveAsync(name);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("name");
        shortcuts.Reads.Should().Be(0, "a caller bug must not cost a Start Menu scan");
    }

    // ---- the edges the desktop will hit ------------------------------------------------------

    [Fact]
    public async Task ListAsync_survives_a_source_that_throws_and_still_returns_the_other()
    {
        // Resolved ambiguity (see the report): one source failing is the same class of event as
        // one package refusing GetAppListEntriesAsync - it is skipped, matching how
        // WindowService.ListAsync treats a virtual-desktop lookup that throws. A catalog that
        // vanished because a single Start Menu folder was unreadable would be worse.
        var packaged = new CountingSource(Packaged("Calculator"));
        var service = new AppCatalogService(
            () => throw new UnauthorizedAccessException("Start Menu folder is not readable"),
            packaged.Read,
            new FakeClock());

        var list = await service.ListAsync();

        list.Select(e => e.Name).Should().Equal("Calculator");
    }

    [Fact]
    public async Task ListAsync_honours_a_cancelled_token()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var shortcuts = new CountingSource(Shortcut("Calculator"));
        var service = Service(shortcuts, new CountingSource(), new FakeClock());

        var act = () => service.ListAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        shortcuts.Reads.Should().Be(0, "a cancelled call does no work at all");
    }

    [Fact]
    public async Task ListAsync_on_two_empty_sources_is_an_empty_catalog()
    {
        var list = await Service(new CountingSource(), new CountingSource(), new FakeClock()).ListAsync();

        list.Should().BeEmpty("a machine with nothing to launch is data, not an error");
    }

    [Fact]
    public async Task ResolveAsync_may_refresh_for_a_miss_again_once_the_ttl_has_turned_over()
    {
        // The "one refresh per miss" budget is spent per catalog, not per process: after the TTL
        // reads the sources again, a miss is allowed to look once more. Without the reset, an app
        // installed after the first miss would stay invisible until the server restarted.
        var shortcuts = new CountingSource(read => read < 4 ? [] : [Shortcut("Calculator")]);
        var clock = new FakeClock();
        var service = Service(shortcuts, new CountingSource(), clock);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ResolveAsync("calc"));
        shortcuts.Reads.Should().Be(2, "the cold read and the one refresh the first miss forced");

        clock.Advance(AppCatalogService.CacheTtl + TimeSpan.FromMilliseconds(1));
        var match = await service.ResolveAsync("calc");

        match.Entry.Name.Should().Be("Calculator");
        shortcuts.Reads.Should().Be(4, "the TTL refresh, then the refresh this new miss is allowed");
    }
}

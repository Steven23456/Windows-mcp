using FlaUI.Core.AutomationElements;
using FluentAssertions;
using WindowsMcp.Services.UiTree;
using Xunit;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// PR #43 fifth review round: <c>UiTraverser.Walk</c> with <c>timeoutsPropagate: true</c> (an
/// observation) lets a <see cref="TimeoutException"/> raised ANYWHERE in the walk out as that same
/// instance - the subtree-cache fetch (V1), any property or pattern read of a node, root or
/// descendant (V2), the children read (V3), and the handle read the owned-window skip makes. A hung
/// provider must not be walked on and reported as observed. With <c>false</c> (every
/// non-observation caller) all of them are swallowed exactly as D-5 always did; a non-timeout
/// failure keeps today's behaviour in strict mode too.
/// <para>
/// Fakes from <see cref="FakeUiWindows"/>: <c>cacheFetchFailure</c> fails the root's
/// <c>FindFirst</c>, <c>propertyFailures</c> one property by name, <c>valuePatternFailure</c> the
/// Value pattern, <c>childrenFailure</c> the children read.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class UiTraverserTimeoutTests : IDisposable
{
    private const int WindowType = 50032;
    private const long Host = 0xAB10;

    private readonly FakeUiWindows _fakes = new();

    public void Dispose() => _fakes.Dispose();

    private AutomationElement Child(string name = "Child", IReadOnlyDictionary<string, Exception>? fails = null,
        Exception? valuePattern = null, long handle = 0) =>
        _fakes.Window(name, handle, bounds: (110, 110, 50, 20), propertyFailures: fails, valuePatternFailure: valuePattern);

    private AutomationElement Root(AutomationElement? child = null, Exception? cacheFetch = null,
        IReadOnlyDictionary<string, Exception>? fails = null, Exception? children = null) =>
        _fakes.Window("Host", Host, controlType: WindowType, bounds: (100, 100, 400, 300),
            children: children is null ? [child ?? Child(), _fakes.Window("Child 2", 0, bounds: (110, 140, 50, 20))] : null,
            childrenFailure: children, cacheFetchFailure: cacheFetch, propertyFailures: fails);

    private IReadOnlyList<UiWalkEntry> Walk(AutomationElement root, bool strict, IReadOnlySet<long>? skip = null) =>
        new UiTraverser(_fakes.Automation).Walk(root, "Host", new ElementBudget(100), skip, timeoutsPropagate: strict);

    private static TimeoutException Hung(string what) => new($"{what} timed out (wmcp-e5)");

    public static TheoryData<string> NodeProperties => new()
    {
        "Name", "ControlType", "BoundingRectangle", "IsOffscreen", "IsEnabled", "HasKeyboardFocus", "IsPassword",
        "AccessKey", "AcceleratorKey",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Harness_a_readable_tree_walks_the_same_in_both_modes(bool strict)
    {
        Walk(Root(), strict).Select(e => e.Node.Name).Should().Equal(["Host", "Child", "Child 2"]);
    }

    // ---- V1: the subtree-cache fetch -----------------------------------------------------------------

    [Fact]
    public void V1_strict_a_timeout_fetching_the_subtree_cache_propagates_even_though_the_root_is_readable()
    {
        var hung = Hung("subtree cache fetch");

        var act = () => Walk(Root(cacheFetch: hung), strict: true);

        act.Should().Throw<TimeoutException>().Which.Should().BeSameAs(hung);
    }

    [Fact]
    public void V1_lenient_a_timeout_fetching_the_subtree_cache_falls_back_to_the_given_root_as_today()
    {
        Walk(Root(cacheFetch: Hung("subtree cache fetch")), strict: false).Select(e => e.Node.Name)
            .Should().Equal(["Host", "Child", "Child 2"]);
    }

    [Fact]
    public void V1_strict_a_non_timeout_failure_fetching_the_cache_still_falls_back_to_the_given_root()
    {
        Walk(Root(cacheFetch: new InvalidOperationException("cache fetch failed (wmcp-e5)")), strict: true).Select(e => e.Node.Name)
            .Should().Equal(["Host", "Child", "Child 2"]);
    }

    // ---- V2: reading a node ----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NodeProperties))]
    public void V2_strict_a_timeout_reading_a_property_of_the_root_propagates(string property)
    {
        var hung = Hung($"root {property} read");

        var act = () => Walk(Root(fails: new Dictionary<string, Exception> { [property] = hung }), strict: true);

        act.Should().Throw<TimeoutException>().Which.Should().BeSameAs(hung);
    }

    [Theory]
    [MemberData(nameof(NodeProperties))]
    public void V2_strict_a_timeout_reading_a_property_of_a_descendant_propagates(string property)
    {
        var hung = Hung($"descendant {property} read");

        var act = () => Walk(Root(Child(fails: new Dictionary<string, Exception> { [property] = hung })), strict: true);

        act.Should().Throw<TimeoutException>().Which.Should().BeSameAs(hung);
    }

    [Theory]
    [MemberData(nameof(NodeProperties))]
    public void V2_lenient_a_timeout_reading_a_property_takes_its_default_or_skips_the_node_as_today(string property)
    {
        var entries = Walk(Root(Child(fails: new Dictionary<string, Exception> { [property] = Hung($"{property} read") })), strict: false);

        entries.Select(e => e.Node.Name).Should().Contain(["Host", "Child 2"], "D-5: one unreadable node never fails a plain walk");
    }

    [Fact]
    public void V2_strict_a_timeout_reading_a_pattern_of_a_descendant_propagates()
    {
        var hung = Hung("Value pattern read");

        var act = () => Walk(Root(Child(valuePattern: hung)), strict: true);

        act.Should().Throw<TimeoutException>().Which.Should().BeSameAs(hung);
    }

    [Fact]
    public void V2_lenient_a_timeout_reading_a_pattern_is_swallowed_as_today()
    {
        Walk(Root(Child(valuePattern: Hung("Value pattern read"))), strict: false).Select(e => e.Node.Name)
            .Should().Equal(["Host", "Child", "Child 2"]);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("IsEnabled")]
    public void V2_strict_a_non_timeout_property_failure_still_takes_its_default(string property)
    {
        var entries = Walk(Root(Child(fails: new Dictionary<string, Exception>
        {
            [property] = new System.Runtime.InteropServices.COMException("read failed (wmcp-e5)", unchecked((int)0x80004005)),
        })), strict: true);

        entries.Should().HaveCount(3, "the node is still there, with that property at its default");
    }

    [Fact]
    public void V2_strict_a_timeout_reading_the_handle_the_owned_window_skip_needs_propagates()
    {
        var hung = Hung("handle read");

        var act = () => Walk(Root(Child(fails: new Dictionary<string, Exception> { ["NativeWindowHandle"] = hung })), strict: true,
            skip: new HashSet<long> { 0xAC01 });

        act.Should().Throw<TimeoutException>().Which.Should().BeSameAs(hung);
    }

    [Fact]
    public void V2_lenient_a_timeout_reading_the_handle_for_the_skip_is_swallowed()
    {
        var act = () => Walk(Root(Child(fails: new Dictionary<string, Exception> { ["NativeWindowHandle"] = Hung("handle read") })), strict: false,
            skip: new HashSet<long> { 0xAC01 });

        act.Should().NotThrow();
    }

    // ---- V3: the children read (pinned at the service in round 3; kept here at the source) ---------

    [Fact]
    public void V3_strict_a_timeout_reading_the_children_propagates()
    {
        var hung = Hung("children read");

        var act = () => Walk(Root(children: hung), strict: true);

        act.Should().Throw<TimeoutException>().Which.Should().BeSameAs(hung);
    }

    [Fact]
    public void V3_lenient_a_timeout_reading_the_children_leaves_the_subtree_out_as_today()
    {
        Walk(Root(children: Hung("children read")), strict: false).Select(e => e.Node.Name).Should().Equal(["Host"]);
    }
}

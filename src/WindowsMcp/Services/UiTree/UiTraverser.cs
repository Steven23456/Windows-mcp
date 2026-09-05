using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>One walked element: the live handle (for the id cache), its facts, and its parent's index in the walk (-1 for the root).</summary>
internal sealed record UiWalkEntry(AutomationElement Element, UiNode Node, int ParentIndex);

/// <summary>
/// A-2's traversal of one top-level window: reads every fact a <see cref="UiNode"/> carries under
/// a single FlaUI <see cref="CacheRequest"/> (one cross-process fetch per subtree instead of one
/// per property — A-4), guards every read (a dead element is skipped, never fatal — D-5), clips
/// each node to the window, drops off-screen and zero-area nodes (keeping an Edit with real
/// bounds — D-7), sanitises text (A-13), and spends the <see cref="ElementBudget"/> once per
/// admitted node, stopping the moment it refuses. Pre-order: a parent always precedes its children.
/// </summary>
internal sealed class UiTraverser
{
    private readonly UIA3Automation _automation;

    internal UiTraverser(UIA3Automation automation) => _automation = automation;

    internal IReadOnlyList<UiWalkEntry> Walk(AutomationElement root, string windowTitle, ElementBudget budget)
    {
        var entries = new List<UiWalkEntry>();

        var request = new CacheRequest
        {
            TreeScope = TreeScope.Subtree,
            AutomationElementMode = AutomationElementMode.Full,
        };
        var p = _automation.PropertyLibrary.Element;
        foreach (var id in new[] { p.Name, p.ControlType, p.BoundingRectangle, p.IsEnabled, p.IsOffscreen,
                                   p.HasKeyboardFocus, p.IsPassword, p.AccessKey, p.AcceleratorKey, p.NativeWindowHandle })
            request.Add(id);
        var pl = _automation.PatternLibrary;
        foreach (var id in new[] { pl.ValuePattern, pl.TogglePattern, pl.RangeValuePattern, pl.ExpandCollapsePattern,
                                   pl.ScrollPattern, pl.LegacyIAccessiblePattern })
            request.Add(id);
        // A cached pattern is not its properties: reading Value/Role/ToggleState inside the request
        // throws PropertyNotCachedException unless each property id is cached as well. The GREEN
        // pass measured 0 scrollables, 0 values and 0 roles over 200 nodes before these were added.
        foreach (var id in new[]
        {
            FlaUI.UIA3.Patterns.ValuePattern.ValueProperty,
            FlaUI.UIA3.Patterns.TogglePattern.ToggleStateProperty,
            FlaUI.UIA3.Patterns.ExpandCollapsePattern.ExpandCollapseStateProperty,
            FlaUI.UIA3.Patterns.RangeValuePattern.ValueProperty,
            FlaUI.UIA3.Patterns.RangeValuePattern.MinimumProperty,
            FlaUI.UIA3.Patterns.RangeValuePattern.MaximumProperty,
            FlaUI.UIA3.Patterns.ScrollPattern.VerticalScrollPercentProperty,
            FlaUI.UIA3.Patterns.ScrollPattern.HorizontalScrollPercentProperty,
            FlaUI.UIA3.Patterns.ScrollPattern.VerticallyScrollableProperty,
            FlaUI.UIA3.Patterns.ScrollPattern.HorizontallyScrollableProperty,
            FlaUI.UIA3.Patterns.LegacyIAccessiblePattern.RoleProperty,
        })
            request.Add(id);

        using (request.Activate())
        {
            // Re-fetch the root under the request so its subtree comes back cached.
            AutomationElement cachedRoot;
            try { cachedRoot = root.FindFirst(TreeScope.Element, TrueCondition.Default) ?? root; }
            catch { cachedRoot = root; }

            var windowRect = ReadBounds(cachedRoot) ?? new Bounds(0, 0, 0, 0);
            Visit(cachedRoot, windowTitle, windowRect, depth: 0, parentIndex: -1, budget, entries);
        }
        return entries;
    }

    private void Visit(AutomationElement el, string window, Bounds windowRect, int depth, int parentIndex,
        ElementBudget budget, List<UiWalkEntry> entries)
    {
        if (budget.Truncated) return;

        UiNode? node;
        try { node = ReadNode(el, window, windowRect, depth); }
        catch { return; }   // died between the fetch and the read: skip it and its subtree

        int myIndex = parentIndex;
        if (node is not null)
        {
            if (!budget.TryTake()) return;
            entries.Add(new UiWalkEntry(el, node, parentIndex));
            myIndex = entries.Count - 1;
        }

        AutomationElement[] children;
        try { children = Children(el); }
        catch { return; }

        foreach (var child in children)
        {
            if (budget.Truncated) return;
            Visit(child, window, windowRect, depth + 1, myIndex, budget, entries);
        }
    }

    /// <summary>Cached children when the request delivered them, a live query otherwise.</summary>
    private static AutomationElement[] Children(AutomationElement el)
    {
        try { return el.CachedChildren; }
        catch { return el.FindAllChildren(); }
    }

    /// <summary>
    /// The node, or null when it is not something the model can see: off-screen (except an Edit
    /// with real bounds), zero-area, or entirely outside the window. Off-screen nodes' subtrees
    /// are skipped by the caller through the null; zero-area containers still get their children walked.
    /// </summary>
    private static UiNode? ReadNode(AutomationElement el, string window, Bounds windowRect, int depth)
    {
        var controlType = Try(() => el.Properties.ControlType.ValueOrDefault.ToString(), "Unknown");
        var rawBounds = ReadBounds(el);
        var offscreen = Try(() => el.Properties.IsOffscreen.ValueOrDefault, false);

        // The root window is entry 0 whatever its geometry says (a maximised window's rect is
        // clipped by the monitor; a minimised one is not walked at all).
        Bounds? bounds = rawBounds;
        if (depth > 0)
        {
            if (rawBounds is null || rawBounds.Width <= 0 || rawBounds.Height <= 0) return null;
            if (offscreen && controlType != nameof(ControlType.Edit)) return null;
            bounds = Clip(rawBounds, windowRect);
            if (bounds is null) return null;
        }

        var value = Try(() => el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault, null);
        var range = Try(() => el.Patterns.RangeValue.PatternOrDefault, null);
        var scroll = Try(() => el.Patterns.Scroll.PatternOrDefault, null);
        ScrollInfo? scrollInfo = null;
        if (scroll is not null)
        {
            // UIA reports -1 (UIA_ScrollPatternNoScroll) for an axis that cannot scroll; the model
            // reads a percentage, so that becomes 0 and the flag says the axis is fixed.
            scrollInfo = Try(() => new ScrollInfo(
                Math.Clamp(scroll.VerticalScrollPercent.ValueOrDefault, 0, 100),
                Math.Clamp(scroll.HorizontalScrollPercent.ValueOrDefault, 0, 100),
                scroll.VerticallyScrollable.ValueOrDefault,
                scroll.HorizontallyScrollable.ValueOrDefault), null);
        }

        return new UiNode(
            Window: window,
            ControlType: controlType,
            Name: UiText.Sanitize(Try(() => el.Properties.Name.ValueOrDefault, null)),
            Bounds: bounds,
            IsEnabled: Try(() => el.Properties.IsEnabled.ValueOrDefault, true),
            IsOffscreen: offscreen,
            HasFocus: Try(() => el.Properties.HasKeyboardFocus.ValueOrDefault, false),
            IsPassword: Try(() => el.Properties.IsPassword.ValueOrDefault, false),
            Value: value is null ? null : UiText.Sanitize(value),
            RangeValue: range is null ? null : Try(() => (double?)range.Value.ValueOrDefault, null),
            RangeMin: range is null ? null : Try(() => (double?)range.Minimum.ValueOrDefault, null),
            RangeMax: range is null ? null : Try(() => (double?)range.Maximum.ValueOrDefault, null),
            ToggleState: Try(() => el.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault.ToString(), null),
            ExpandState: Try(() => el.Patterns.ExpandCollapse.PatternOrDefault?.ExpandCollapseState.ValueOrDefault.ToString(), null),
            AccessKey: Try(() => el.Properties.AccessKey.ValueOrDefault, null),
            AcceleratorKey: Try(() => el.Properties.AcceleratorKey.ValueOrDefault, null),
            LegacyRole: Try(() => LegacyRoleName(el), null),
            Scroll: scrollInfo,
            Depth: depth);
    }

    /// <summary>ROLE_SYSTEM_PUSHBUTTON → "pushbutton": the spelling <see cref="UiClassifier.InteractiveLegacyRoles"/> uses.</summary>
    private static string? LegacyRoleName(AutomationElement el)
    {
        var legacy = el.Patterns.LegacyIAccessible.PatternOrDefault;
        if (legacy is null) return null;
        var role = legacy.Role.ValueOrDefault.ToString();
        const string prefix = "ROLE_SYSTEM_";
        return (role.StartsWith(prefix, StringComparison.Ordinal) ? role[prefix.Length..] : role).ToLowerInvariant();
    }

    private static Bounds? ReadBounds(AutomationElement el)
    {
        try
        {
            var b = el.Properties.BoundingRectangle.ValueOrDefault;
            return new Bounds((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
        }
        catch { return null; }
    }

    /// <summary>The part of <paramref name="b"/> inside <paramref name="window"/>; null when nothing is.</summary>
    internal static Bounds? Clip(Bounds b, Bounds window)
    {
        if (window.Width <= 0 || window.Height <= 0) return b;   // unknown window rect: trust the node
        int l = Math.Max(b.X, window.X), t = Math.Max(b.Y, window.Y);
        int r = Math.Min(b.X + b.Width, window.X + window.Width);
        int bt = Math.Min(b.Y + b.Height, window.Y + window.Height);
        if (r <= l || bt <= t) return null;
        return new Bounds(l, t, r - l, bt - t);
    }

    private static T Try<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}

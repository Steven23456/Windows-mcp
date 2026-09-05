using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>
/// A-2: everything one UIA element contributes to a snapshot, read once under a CacheRequest and
/// then never touched again. Pure data — the classifier, the budget and the renderer all work on
/// these, so every rule in A-2 is provable with no desktop attached (roadmap C10).
/// </summary>
/// <param name="Window">Title of the top-level window the element was found under.</param>
/// <param name="ControlType">UIA control type NAME ("Button", "Edit"), not the enum.</param>
/// <param name="LegacyRole">
/// LegacyIAccessible role name ("pushbutton", "text"), null when the pattern is absent. The
/// fallback D-6 deferred: a Chromium/Qt <c>Custom</c> element only says what it is here.
/// </param>
/// <param name="Depth">Distance from the window root; 0 is the window itself.</param>
internal sealed record UiNode(
    string Window,
    string ControlType,
    string Name,
    Bounds? Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    bool HasFocus,
    bool IsPassword,
    string? Value,
    double? RangeValue,
    double? RangeMin,
    double? RangeMax,
    string? ToggleState,
    string? ExpandState,
    string? AccessKey,
    string? AcceleratorKey,
    string? LegacyRole,
    ScrollInfo? Scroll,
    int Depth);

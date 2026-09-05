using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Tests.Services.UiTree;

/// <summary>
/// Builds a <see cref="UiNode"/> that is a plain enabled on-screen button, overridable field by
/// field. Every classifier row below is "the baseline, except …", which is what keeps the rows
/// readable as requirements.
/// </summary>
internal static class NodeFixtures
{
    internal static UiNode Node(
        string controlType = "Button",
        string name = "Save",
        string window = "Untitled - Notepad",
        Bounds? bounds = null,
        bool isEnabled = true,
        bool isOffscreen = false,
        bool hasFocus = false,
        bool isPassword = false,
        string? value = null,
        double? rangeValue = null,
        double? rangeMin = null,
        double? rangeMax = null,
        string? toggleState = null,
        string? expandState = null,
        string? accessKey = null,
        string? acceleratorKey = null,
        string? legacyRole = null,
        ScrollInfo? scroll = null,
        int depth = 1)
        => new(window, controlType, name, bounds ?? new Bounds(600, 380, 24, 16), isEnabled, isOffscreen,
            hasFocus, isPassword, value, rangeValue, rangeMin, rangeMax, toggleState, expandState,
            accessKey, acceleratorKey, legacyRole, scroll, depth);
}

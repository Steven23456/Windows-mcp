using FlaUI.Core.Definitions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>What a node is to the model: something to act on, something to read, or scaffolding.</summary>
internal enum UiRole { Interactive, Informative, Structural }

/// <summary>
/// A-2's classifier: which nodes are interactive, what the default action on each is, and the
/// small geometry/metadata helpers the renderer needs. Pure — a <see cref="UiNode"/> in, a
/// verdict out — so every rule is unit-tested on hand-built nodes (roadmap C10). Owns the D-6
/// interactive set; <c>find_element(kind:interactive)</c> reads it from here.
/// </summary>
internal static class UiClassifier
{
    /// <summary>
    /// D-6: upstream's <c>INTERACTIVE_CONTROL_TYPE_NAMES</c> (<c>tree/config.py</c>) plus
    /// <see cref="ControlType.Document"/>. Upstream's <c>TextBox</c> is omitted — there is no such
    /// UIA control type; it is <see cref="ControlType.Edit"/>, already here. <c>Document</c> is in
    /// because a text area you type into is something you interact with (modern Notepad's editor
    /// is a Document, not an Edit). One home, so the find path and the snapshot cannot drift.
    /// </summary>
    internal static readonly ControlType[] InteractiveControlTypes =
    [
        ControlType.Button, ControlType.ListItem, ControlType.MenuItem, ControlType.Edit,
        ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Hyperlink,
        ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
        ControlType.HeaderItem, ControlType.Spinner, ControlType.Slider, ControlType.ScrollBar,
        ControlType.Document,
    ];

    /// <summary>The same 17, as the names a <see cref="UiNode.ControlType"/> carries.</summary>
    internal static readonly string[] InteractiveControlTypeNames =
        InteractiveControlTypes.Select(t => t.ToString()).ToArray();

    /// <summary>
    /// Upstream's LegacyIAccessible fallback: MSAA roles that mean "interactive" on elements whose
    /// UIA control type is Custom or otherwise uninformative (web content, owner-drawn controls).
    /// <c>text</c> (ROLE_SYSTEM_TEXT) is editable text only when the node also carries a value —
    /// static text has that role too. See <see cref="Classify"/>.
    /// </summary>
    internal static readonly HashSet<string> InteractiveLegacyRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pushbutton", "checkbutton", "radiobutton", "combobox", "link", "menuitem", "listitem",
        "pagetab", "slider", "spinbutton", "outlineitem", "cell", "splitbutton", "buttondropdown",
        "buttonmenu", "text",
    };

    /// <summary>Read-only content: worth reporting, never worth clicking. HeaderItem is NOT here — a column header sorts.</summary>
    internal static readonly string[] InformativeControlTypes =
        ["Text", "Image", "StatusBar", "ProgressBar", "ToolTip", "Header"];

    private static readonly HashSet<string> InteractiveNameSet = new(InteractiveControlTypeNames, StringComparer.Ordinal);
    private static readonly HashSet<string> InformativeNameSet = new(InformativeControlTypes, StringComparer.Ordinal);

    /// <summary>Control type first (it is authoritative), then the legacy role, then the read-only types.</summary>
    internal static UiRole Classify(UiNode n)
    {
        if (InteractiveNameSet.Contains(n.ControlType)) return UiRole.Interactive;
        if (HasInteractiveRole(n)) return UiRole.Interactive;
        if (InformativeNameSet.Contains(n.ControlType)) return UiRole.Informative;
        return UiRole.Structural;
    }

    private static bool HasInteractiveRole(UiNode n)
    {
        if (n.LegacyRole is null || !InteractiveLegacyRoles.Contains(n.LegacyRole)) return false;
        // ROLE_SYSTEM_TEXT is both an edit box and a static label; only the one with a value is editable.
        return !n.LegacyRole.Equals("text", StringComparison.OrdinalIgnoreCase) || n.Value is not null;
    }

    /// <summary>
    /// The verb the model should reach for: upstream's <c>_ACTION_MAP</c>, with Document → fill
    /// (a Document is the thing you type into; scrolling it is advertised in the scrollable list).
    /// Control type wins over the legacy role; anything else is a click.
    /// </summary>
    internal static string ActionFor(UiNode n)
    {
        switch (n.ControlType)
        {
            case "Edit" or "Document": return "fill";
            case "CheckBox": return "toggle";
            case "ComboBox": return "select";
            case "Slider" or "Spinner": return "slide";
            case "ScrollBar": return "scroll";
        }
        if (InteractiveNameSet.Contains(n.ControlType)) return "click";

        return n.LegacyRole?.ToLowerInvariant() switch
        {
            "checkbutton" => "toggle",
            "combobox" => "select",
            "slider" or "spinbutton" => "slide",
            "text" => "fill",
            _ => "click",
        };
    }

    /// <summary>A scroll pattern that can actually move on at least one axis (A-3).</summary>
    internal static bool IsScrollable(UiNode n)
        => n.Scroll is { } s && (s.VerticallyScrollable || s.HorizontallyScrollable);

    /// <summary>The point <c>click</c> should aim at: the middle of the bounds, integer division.</summary>
    internal static (int X, int Y) CenterOf(Bounds b) => (b.X + b.Width / 2, b.Y + b.Height / 2);

    /// <summary>The accelerator (Ctrl+S) over the access key (Alt+F), trimmed; null when neither says anything.</summary>
    internal static string? ShortcutOf(UiNode n)
    {
        if (!string.IsNullOrWhiteSpace(n.AcceleratorKey)) return n.AcceleratorKey.Trim();
        if (!string.IsNullOrWhiteSpace(n.AccessKey)) return n.AccessKey.Trim();
        return null;
    }
}

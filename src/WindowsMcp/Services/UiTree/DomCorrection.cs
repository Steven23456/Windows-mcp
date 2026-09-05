using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services.UiTree;

/// <summary>
/// A-5 phase 1: the pure part of browser DOM mode — upstream's <c>_dom_correction()</c>. Chromium
/// exposes proper UIA control types for page content, so there is no role map; what is left are
/// the three corrections a page walk needs and the projection of a walked page onto
/// <see cref="SnapshotPage"/>. No UIA here, so every rule is provable on hand-built
/// <see cref="UiNode"/>s with no browser attached (roadmap C10).
/// </summary>
/// <remarks>
/// The walk entries arrive as (node, parent index) pairs rather than <c>UiWalkEntry</c> so these
/// rules can be tested without a live <c>AutomationElement</c>; the service passes its entries
/// through unchanged.
/// </remarks>
internal static class DomCorrection
{
    /// <summary>
    /// What a page-less browser window's <see cref="SnapshotPage.Note"/> says: both that no page
    /// document was found and what was walked instead.
    /// </summary>
    internal const string NoPageNote =
        "no page document found under this window; walked the whole window instead";

    /// <summary>
    /// Correction 1: the page document itself is never an INTERACTIVE element. A Document is
    /// "fill" in the desktop classifier (modern Notepad's editor is one), but the page is not a
    /// control — it still gets its id and still appears in the scrollable list.
    /// </summary>
    /// <param name="parentIndex">The node's parent index in the walk; negative for the walk root.</param>
    internal static bool SuppressesInteractive(UiNode node, int parentIndex)
        => parentIndex < 0 && node.ControlType == "Document";

    /// <summary>
    /// Corrections 2 and 3, applied while collecting the page's visible text: the Names of the
    /// Text nodes in walk (document) order, minus the ones that only repeat their interactive
    /// parent's label and the ones with nothing to say.
    /// </summary>
    internal static string[] PageText(IReadOnlyList<(UiNode Node, int ParentIndex)> entries)
    {
        var text = new List<string>();
        foreach (var (node, parentIndex) in entries)
        {
            if (node.ControlType != "Text" || string.IsNullOrWhiteSpace(node.Name)) continue;
            if (parentIndex >= 0 && parentIndex < entries.Count)
            {
                // The label of the control it sits in, not content: the interactive row already says it.
                var parent = entries[parentIndex].Node;
                if (UiClassifier.Classify(parent) == UiRole.Interactive
                    && string.Equals(parent.Name, node.Name, StringComparison.Ordinal))
                    continue;
            }
            text.Add(node.Name);
        }
        return text.ToArray();
    }

    /// <summary>
    /// The page one browser window contributes: <paramref name="entries"/>[0] is the page document
    /// (the walk root), <paramref name="documentId"/> the id it was issued.
    /// </summary>
    internal static SnapshotPage PageFor(string documentId, IReadOnlyList<(UiNode Node, int ParentIndex)> entries)
    {
        if (entries.Count == 0)
            throw new ArgumentException("A page needs its document: entries[0] must be the walked RootWebArea.", nameof(entries));
        var doc = entries[0].Node;
        return new SnapshotPage(doc.Window, documentId, doc.Name, doc.Value, doc.Scroll, PageText(entries), Note: null);
    }

    /// <summary>The page a browser window with no page document contributes: nothing but the note.</summary>
    internal static SnapshotPage NoPage(string window)
        => new(window, null, null, null, null, [], NoPageNote);
}

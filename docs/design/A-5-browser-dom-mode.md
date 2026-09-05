# A-5 (phase 1) — browser DOM mode on `snapshot`, Chromium only

**Checklist item:** [A-5](../upstream-parity-checklist.md#a-5--browser-dom-mode-use_dom--p2--l) ·
**Roadmap:** [A-roadmap](A-roadmap.md) phase 5, last item — phase 1 only (Chromium through
UI Automation); phase 2 (Firefox through MSAA/IA2) is the documented follow-up ·
**Status:** implemented 2026-09-05 (build clean, 2017/2017 headless tests green, 12/12 Edge-backed
desktop tests green, the whole 96-test desktop bracket green once the pointer-moving classes
were serialised — see CHANGELOG [Unreleased]) ·
**Effort:** ~1 day including the probe and the RED/GREEN passes.

## Problem

A snapshot of a browser window walked the browser: the tab strip, the address bar, the toolbar
buttons, and only then the page, with the page's own content mixed into the same list and no way
to read the page's text. Upstream finds the element whose AutomationId is `RootWebArea`, walks
only that subtree, drops the chrome, collects the text nodes and reports the document's scroll
percent, with a `_dom_correction` step for Chromium's quirks.

## What the probe found (Edge, an `--app=` window on a local page)

- The page is one element: ControlType **Document**, AutomationId **`RootWebArea`**, Name = the
  page `<title>`, ValuePattern = the **URL**, ScrollPattern present (vertical only). Its ancestors
  are ~10 anonymous Panes; a normal window has the tab strip and address bar as sibling subtrees,
  so a walk that starts at the document never sees them.
- Chromium exposes proper UIA control types for page content — Text, Hyperlink (Value = href),
  Button, Edit (Value = contents), CheckBox (ToggleState), ComboBox (Value = the selected option),
  List/ListItem — so the existing `UiClassifier` is the role map and the roadmap's
  LegacyIAccessible mapping is not needed for Chromium.
- **Chromium builds its accessibility tree lazily**, on the first client query, and fills it in
  after answering: the first find by AutomationId came back empty on a page that was there, and
  the same find succeeded once a plain Document query had woken the tree.
- Below-the-fold text is `IsOffscreen` and is dropped by `UiTraverser` like every off-screen node.

## Decision

- **`snapshot(use_dom:true)` walks browser windows from the page.** For each target window whose
  inventory row says `IsBrowser` (A-1's process-name set: chrome/msedge/brave/opera/vivaldi/
  firefox), `FindPageDocument` looks for the first descendant that is a Document **and** has
  AutomationId `RootWebArea`, three attempts with a plain Document query as the nudge and a 150 ms
  pause between them, never after the last. Found: the walk starts there (`UiTraverser.Walk`
  unchanged; the document is entry 0 of that window's entries, clipped to the document's rect),
  so the chrome is excluded by construction. Not found (still loading, Firefox, a non-web page):
  the window is walked whole, as without the flag, and its page entry carries the one-sentence
  note that says both facts. Non-browser windows walk exactly as before; without the flag
  **nothing** changes (same walks, same ids, `Pages` null and JSON-invisible — the A-14 rule).
- **Three corrections, pure** (`DomCorrection`, 100 % covered on hand-built nodes; it takes
  `(UiNode, parentIndex)` pairs, not `UiWalkEntry`, because a live `AutomationElement` cannot be
  faked): (1) the page document at the walk root is never an **interactive** element — a Document
  is "fill" in the desktop classifier, but the page is not a control; it keeps its `el_N` id and
  its **scrollable** row with the percentages (upstream's `dom_node`); a Document inside the page
  (an iframe) is left alone; (2) a Text node whose Name equals its **interactive** parent's Name,
  ordinal and case-sensitive, is that control's label, not content — a repeat under a Group or
  Pane is kept, and there is no global dedupe, a page that says "Item one" twice says it twice;
  (3) blank text contributes nothing. Nothing else is changed: ListItem stays interactive, parity
  with upstream's set.
- **`Pages`** on the result, one `SnapshotPage(Window, DocumentId, Title, Url, Scroll, Text, Note)`
  per browser window in walk order: the document's id, Name, Value and ScrollInfo, and the visible
  Text nodes' Names in document order. The text form prints it after the scrollable list and
  before the truncation note and the timing line — ids and scroll targets first, then content,
  then diagnostics:

  ```
  Pages (1):
    el_7 "A5 Probe Page" http://127.0.0.1:9999/a5  [v: 0%]
      Probe heading
      First paragraph of body text.
    window "Other Browser": no page document found under this window; walked the whole window instead
  ```

  The `[v: N%]` tag is omitted when the document has no scroll pattern, the URL when it has no
  value; title, window and every text line go through the renderer's escaping, so a hostile
  `<title>` cannot forge a row.
- **Electron and WebView2 apps are not browsers.** `IsBrowser` is decided by process name, so
  Claude Desktop, VS Code or Teams — which carry a real `RootWebArea` that `FindPageDocument`
  finds — are walked whole under `use_dom` and contribute no page. That is the intended default
  (the value of DOM mode is dropping the *browser* chrome, which those apps do not have) and it is
  pinned by a test; it also bounds what `use_dom` can ever read.

## Changes

- `Abstractions`: `SnapshotRequest +UseDom`; `SnapshotPage` (new); `SnapshotResult +Pages`
  (JSON-omitted when null).
- `Services/UiTree/DomCorrection.cs` (new: `NoPageNote`, `SuppressesInteractive`, `PageText`,
  `PageFor`, `NoPage`); `Services/UIAutomationService.cs` (`FindPageDocument`, the per-window
  `DomState`, the page walk and the page list); `Services/UiTree/SnapshotRenderer.cs` (the Pages
  block).
- `Tools/UIAutomationTools.cs` — `use_dom` forwarded instead of refused; both descriptions
  rewritten (RootWebArea, Chromium, the Pages section, Firefox as the follow-up).
- Tests: `Fixtures/EdgeFixture.cs` (an `msedge --app=` window on `LocalHttpServerFixture`'s new
  `/a5` probe page in a throwaway profile, killed by its own `--user-data-dir`; a **collection**
  fixture, because two classes each owning an Edge ran in parallel and the window-title match
  found both pages).

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | `UseDom` last and default false; `SnapshotPage` shape; `Pages` after `Stages`, null by default and absent from JSON, `[]` written when empty, order kept | `SnapshotDtosTests` (8) | Unit |
| R2 | The three corrections: only a Document at the root is suppressed, every page control and an inner Document left alone; Text nodes only, walk order; the interactive-parent label rule (kept for a differing child, a non-interactive parent, a case difference, no parent, an out-of-range parent), blank dropped, no global dedupe; `PageFor` fields, null scroll, document-only page, the rules not bypassable, `[]` is a programming error; `NoPage` says both facts with the one wording | `DomCorrectionTests` (23 methods) | Unit |
| R3 | `use_dom:false` → `Pages` null (mocked and real desktop); no browser in scope → `[]`, the window still walked; argument rules first; on this session every page belongs to a browser window, in walk order, `Text` never null; the finder gives up on a window with no web content within 2 s, pauses between attempts and never after the last, and finds the document in an Electron window while `use_dom` still leaves that window alone | `UIAutomationDomSnapshotUnitTests` (3), `UIAutomationDomSnapshotIntegrationTests` (7) | Unit / Integration |
| R4 | Real Edge: title, URL, `DocumentId` resolving through `get_element` to a Document, scroll; the document scrollable and never interactive; every probe-page control with its action and value; the DOM element set a subset of the whole-window set and `Pages` null without the flag; visible text in document order without the paragraph below the fold; labels not repeated; foreground scope; a budget-truncated page still reported; `include_tree` rooted at the document; the rendered block; the same over real HTTP | `UIAutomationDomSnapshotTests` (10), `HttpTransportDomSnapshotTests` (1) | UIAutomation |
| R5 | Renderer: the exact block and its position, nothing when null, `Pages (0):`, the scroll tag omitted / rounded like the scrollable line, the note line, mixed order, escaping, a text-less page, a null title as `""`, a null URL with no dangling space | `SnapshotRendererTests` (16) | Unit |
| R6 | Tool: `use_dom` forwarded both ways, default off, no longer refused, still the last parameter; JSON and text carry the pages, an empty block, nothing when not asked; both descriptions | `UIAutomationToolsTests` (13) | Unit |
| R7 | The flag and `Pages` cross JSON-RPC over HTTP (mocked service; the real sibling is R4's) | `HttpTransportTests` (1) | Integration |

Coverage: `DomCorrection`, `SnapshotRenderer`, `UIAutomationTools` 100 % line and branch;
`FindPageDocument` 94 % line / 100 % branch (the rest is the catch for a window that dies
mid-find); the page-walk lines in `SnapshotAsync` are desktop-only and are covered by R4. Bite
check: seven breaks — correction 1 disabled, correction 2 case-insensitive, `PageText` collecting
every named node, the block before the scrollable list, the tool forwarding `!use_dom`, `[]`
instead of null when off, the finder never pausing — all caught, two of them only by tests the
GREEN pass added. The GREEN pass also found the null-URL dangling space in the renderer (fixed)
and three defects in its own RED tests (a reason string passed as an expected element, a
"non-browser window" that was the Electron Claude app, and the two-Edge race), all fixed there.

## Deviations and follow-ups

- **Firefox (phase 2)** is not implemented: a Firefox window under `use_dom` is walked whole and
  its page entry says so. The service's choice of that branch is the one path no test drives —
  `EdgeFixture` always has a page, and about:blank and the new-tab page both carry a
  `RootWebArea` — so it belongs in the live e2e sweep: open Firefox, call `snapshot(use_dom:true)`,
  expect the note.
- **A page that is still loading** comes back as "no page" after the ~450 ms retry budget; the
  entry says so and the caller retries. The budget is small on purpose (a desktop full of
  non-browser windows never pays it, and a browser window pays it once).
- `scrape(source:dom)` (C-5) and `wait_for(use_dom)` (B-6) are not wired; both can consume
  `Pages` as it stands.
- The whole desktop bracket exposed a pre-existing test-infrastructure race unrelated to A-5:
  the pointer-moving screenshot classes ran in parallel and fought over the mouse. They now share
  one collection.

# C-5 — `scrape`: DOM source, query focus, a summary through the client's model

**Checklist item:** [C-5](../upstream-parity-checklist.md#c-5--scrape-dom-source-query-focus-mcp-sampling-summary--p2--m) ·
**Roadmap:** [C-roadmap](C-roadmap.md) phase 3, last item — decision R8 (JSON result, the
live tab read through A-5, a summary only on request and only through MCP sampling) ·
**Status:** implemented 2026-09-08 (build clean, headless suite green, the two Edge-backed
desktop tests green against a real browser window, the sampling round trip proven over an
in-process stream transport; two review rounds — see CHANGELOG [Unreleased]) ·
**Effort:** ~4 h plus the review round, including the sampling round trip proven over an
in-process stream transport.

## Problem

`scrape(url)` fetches a URL, converts the HTML to Markdown and returns the whole string: no
title, no size, no way to cap it, no way to read the page that is already open in the browser
(which A-5 can now walk), and no way to hand the text to a model for the one-paragraph answer the
agent usually wanted. Upstream has `Scrape(url, query?, use_dom, use_sampling)`: the live tab's
text with "Reached top / Scroll down to see more" hints, and a client-side summary through
`ctx.sample()` when the client supports sampling.

## Decision

### The signature and the result

- **`scrape(url?, query?, source = "http", summarize = false, max_chars = 100000, window?)`** →
  JSON `ScrapeResult { Source, Url, Title, Chars, Truncated, Content, Summarized, Model, Note }`.
  `Chars` is the size of the text **before** the cap, `Truncated` says the cap cut it,
  `Content` is what came back (the summary when `Summarized`, else the text), `Model` is the
  client's model name when `Summarized`, and `Note` says why a request was not honoured. The tool
  stays `ReadOnly = true, OpenWorld = true`. The string → JSON change is a contract change
  (CHANGELOG *Changed*).
- **Refusals, before anything is fetched or walked** (`ArgumentException`, each naming the
  parameter): `source` other than `http` | `dom` (case-insensitive); `max_chars` outside
  `1…1000000` (`0` is not "all"); `source:http` without `url`, or with `window`; `source:dom`
  with `url` (it reads the page that is open; there is nothing to fetch); `query` without
  `summarize:true` (the summary is what answers it).

### `source: http`

- **`IWebService.ScrapeAsync(url, maxChars, ct)`** → `ScrapeResult`, replacing the string
  overload. The private-address check is unchanged and still runs first. The service reads the
  response, takes `Title` from the first `<title>` (entities decoded, whitespace collapsed; null
  when absent or blank), converts the HTML to Markdown as before (`ReverseMarkdown`), measures
  `Chars`, and cuts to `maxChars`. `Url` is the URL the content came from **after redirects**
  (the response's request URI), so a `http://` that landed on `https://` says so.

### `source: dom`

- **The window.** `window:<title>` walks that window (`SnapshotScope.Window`); without it the
  tool takes the **frontmost Chromium window** from the A-1 inventory (`IWindowService.ListAsync
  (includeMinimized:false)`, first row whose process `WindowFilter.IsChromium` accepts — chrome,
  msedge, brave, opera, vivaldi — in z-order): the window an agent means by "the open tab"
  whether or not it is in front of the terminal the agent runs from. Firefox is a browser in the
  inventory but exposes no page document to UI Automation, so it is skipped, and when it is the
  only browser open the refusal names it and offers `source:http`. No browser window open is an
  `InvalidOperationException` that says so and lists the open titles; a named window that yields
  no page (`Pages` empty) is refused naming the window and the Chromium-only rule, or — when the
  walk ran out of element budget before reaching a page because another window matched the
  title first — the budget.
- **The walk** is one `IUIAutomationService.SnapshotAsync(new SnapshotRequest(Window, title,
  UseDom: true))` and the page is the **first entry in `Pages` with a `DocumentId`**. `Title` and
  `Url` are the document's Name and Value. A page carrying only A-5's note (no `RootWebArea`
  found: still loading, Firefox, a non-web page) never has text, so it is **refused** with the
  note in the message rather than returned empty. A walk the element budget cut short is
  reported: `Truncated: true` and a `Note` naming the budget and `--max-tree-elements`.
- **The text** is a pure `Services/DomPage.Render(page)`: the visible `Text` lines joined with
  `\n`, then — when the document scrolls vertically — a blank line and one hint:
  `VerticalPercent <= 0` → "Reached top of the page; scroll down to see more."; `>= 100` →
  "Reached bottom of the page; scroll up to see more."; otherwise "Scrolled N% down the page;
  scroll up or down to see more." (N rounded). No scroll pattern, or not vertically scrollable, →
  no hint: the whole page is on screen. `Chars` and `max_chars` apply to the rendered text.

### `summarize: true`

- **Only through MCP sampling, and only when the client declared it.** The tool takes the
  SDK-bound `McpServer` (not in the input schema) behind an internal `ISamplingClient` seam
  (`Supported` = `ClientCapabilities?.Sampling is not null`; `SampleAsync` =
  `McpServer.SampleAsync`), so the unit tests fake the seam and an in-process stream-transport
  test proves the real one. Sampling costs the *client* a model call; the default stays `false`.
- **Not supported** → the text comes back with `Summarized: false` and `Note: "summarize:true
  was ignored: this client did not declare the sampling capability, so the content is returned
  as is"`. This is the path Claude Code runs.
- **Sampling needs a session, and the HTTP transport keeps none.** `BuildHttpApp` runs
  Streamable HTTP stateless by an earlier decision (a fresh `McpServer` per request, a restart
  invisible to the client), so a per-request server never knows what the client declared at
  initialize and has no stream for a sampling reply to come back on. Rather than blame the
  client, the host registers `TransportOptions(Stateless)` (the `ScreenshotOptions` pattern)
  and over HTTP `summarize:true` returns the text with `Note: "summarize:true was ignored: this
  server is running over its stateless HTTP transport, which keeps no session to carry a
  sampling request to the client; run the server over stdio for summaries. The content is
  returned as is"` — the client is never asked. Over stdio (Claude Desktop, Claude Code) the
  session lives as long as the process and the capability check above decides.
- **Supported** → a pure `Services/ScrapeSummary.Request(content, query, truncated)` builds
  the `CreateMessageRequestParams`: `SystemPrompt` strips navigation, headers, footers, cookie
  and consent banners, advertisements and repeated boilerplate, keeps names, numbers, dates,
  prices and quoted values verbatim, invents nothing, and either answers `query` from the page
  (quoting the passages, saying plainly when the page does not answer it) or, without a query,
  summarises the page faithfully — and, when `truncated`, says the text is only the beginning
  of a longer page and must not be read as the page lacking what may lie beyond the cut; one
  user message holding the (already capped) content; `MaxTokens = ScrapeSummary.MaxTokens`
  (2048). The call is bounded by `WebTools.SamplingTimeout` (120 s) on a token linked to the
  caller's. The reply's text blocks joined with `\n` become `Content`, `Summarized: true`,
  `Model` = the reply's model. Blank content is not sent (`Note: "nothing to summarise: the page
  has no text"`); a reply with no text block, a client that does not answer in time, or any
  failure inside the client returns the text with `Summarized: false` and a `Note` naming the
  cause — the page is not lost because the summary failed. Only the caller's cancellation
  propagates.

## Changes

- `Abstractions/Models/WebDtos.cs` — `ScrapeResult` and `TransportOptions` (new records).
- `Abstractions/IWebService.cs` — `ScrapeAsync(url, maxChars, ct)` → `ScrapeResult`.
- `Services/WebService.cs` — title (AngleSharp's `IDocument.Title`), redirect URL with any user
  info stripped, cap; a failed fetch is caller-facing (`HttpRequestException` →
  `InvalidOperationException` naming the URL, the client's own timeout → `TimeoutException`)
  instead of the masked "An error occurred invoking 'scrape'"; http/https only, decided before
  the address check, for `scrape` and `http_request` alike; `MaxNestingDepth` (300) measured
  iteratively before the converter runs; one `HttpClient` per service instance so a test can give
  it a short timeout. `Services/DomPage.cs`, `Services/ScrapeSummary.cs` and
  `Services/TextCap.cs` (the one surrogate-safe cut both sources use) — new, pure.
- `Services/WindowFilter.cs` — `ChromiumProcesses` and `IsChromium` beside `IsBrowser`, sharing
  one `BareName` (round 2, F4).
- `Tools/WebTools.cs` — the parameters, the refusals, the DOM walk, the sampling call; the
  internal `ISamplingClient` and its `McpServer` adapter (`Tools/ISamplingClient.cs`); the
  constructor gains `IUIAutomationService`, `IWindowService` and an optional `TransportOptions`.
- `Hosting/WindowsMcpHost.cs` — registers `TransportOptions` from `ServerOptions.Transport`
  (no new service; the count stays 39).

## Tests (test-agent RED → GREEN)

| # | Requirement | Test(s) | Category |
|---|---|---|---|
| R1 | The refusals, each naming its parameter and firing before anything is fetched or walked: unknown `source` (`ftp`, `""`, `web`); `max_chars` 0 / −1 / 1 000 001; `http` without `url` or with `window`; `dom` with `url`; `query` without `summarize`; the order source → max_chars → url/window → query when several are broken; `source` matched in any case and reported lower-case | `WebToolsTests` | Unit |
| R2 | `http` forwards `url`, `max_chars` (1 / 5000 / 1 000 000) and the token once, never cuts twice, and reports every field including the post-redirect `Url` | `WebToolsTests` | Unit |
| R3 | `WebService.ScrapeAsync` on a loopback server: `Source`, `Url`, `Title` (`"One"`, null without a `<title>`, `A &amp; B\n C` → `A & B C`), `Chars == Content.Length`, the URL after a 302, the cap (`Truncated`, `Chars` the pre-cap size, exactly at the cap not truncated), non-positive `maxChars` refused, a real 404 and a refused connection caller-facing and naming the URL, a host that never answers a `TimeoutException` naming the seconds (and the same service still fetching the next page afterwards), a cancelled call a cancellation and not a timeout, a host that does not resolve caller-facing; the private-address and malformed-URL refusals still first; `HtmlTitle` on thirteen shapes (the first `<title>`, an SVG tooltip before it, one inside a comment, one inside a script string, entities before whitespace, any case or attributes, unclosed, absent, blank); `TextCap.Cut` on twelve (surrogate step-back, cap 0/1, CRLF) | `WebServiceScrapeTests` (27), `WebServiceTests`, `TextCapTests` (12) | Integration / Unit |
| R4 | `dom`: `window:` walks that window (`Scope==Window`, `UseDom`) and never lists the desktop; without it `ListAsync(false,false)` once and the first `IsBrowser` row in z-order; no browser open → refusal listing every title (`(none)` for an empty desktop) and offering `source:http`; `Pages` empty or null → refusal naming the window, Chromium and the alternative; a page's own note comes back in `Note` with the text that was walked; the rendered text capped with `Chars` the pre-cap size | `WebToolsTests` | Unit |
| R5 | `DomPage.Hint` at every threshold (null pattern, not vertical, horizontal-only, ≤ 0, ≥ 100, rounded between); `Render` joins, the blank line before the hint, no trailing blank, empty-page cases, order kept | `DomPageTests` (12) | Unit |
| R6 | `ScrapeSummary`: the prompt names navigation / cookie / boilerplate / advertisements, keeps values verbatim, summarises without a query and never says "question", carries the query and allows "does not answer" with one; `Request` = the pure prompt, one user text block, `MaxTokens` 2048 | `ScrapeSummaryTests` (7) | Unit |
| R7 | `summarize`: no capability → text + the capability note, seam never asked; supported → the request carries the capped content and the query, the reply's text blocks joined become `Content` with `Model` and `Summarized:true`, `Chars`/`Truncated` still describe the page; blank content never sent; a reply with no text, an `McpException` → the text with a `Note`; cancellation propagates; dom text summarised after the cap; a page's own note joined readably with any of the three | `WebToolsTests` | Unit |
| R8 | The stateless transport: `TransportOptions(Stateless:true)` → the stateless note and the seam never consulted even when supported; no note without `summarize`; `Stdio` behaves like no option; the host registers it from `ServerOptions.Transport` for both kinds as a singleton and `WebTools` resolves it; over the real HTTP host a client that declares sampling with a handler still gets the note and its handler is never called | `WebToolsTests`, `WindowsMcpHostTests`, `HttpTransportTests` | Unit / Integration |
| R9 | The real round trip over a session-keeping transport (`StreamServerTransport` / `StreamClientTransport` over pipes, the host's own `McpServerOptions`): `Summarized:true`, `Model:"canned-model"`, the query in `SystemPrompt`, the page as the one user block; the capability note for a client with no handler; `summarize:false` never samples; 69 tools with `scrape` `ReadOnly` / `OpenWorld` | `StreamTransportTests` (4) | Integration |
| R10 | The schema advertises the six parameters, never `server`, and requires nothing | `HttpTransportTests` | Integration |
| R11 | Real Edge: `source:dom, window:` → the probe page's title, URL, visible text without the below-the-fold paragraph, the top-of-page hint; without `window` after bringing Edge to the front | `WebToolsDomDesktopTests` (2) | UIAutomation |
| R12 | Round 2, F5: the scheme decided before the address, on **both** methods — `ftp:`, `file:`, `data:`, `ws:`, `javascript:` refused naming the scheme, `HTTP:`/`HttpS:` still fetched, a private host refused for the address and not the scheme; `http_request` over the loopback server still reports the status it was answered with, sends the method / headers / body it was given (and no body when none), reports response and content headers together, returns a failure status as a result rather than throwing, and refuses a `file:` URL before it sends anything | `WebServiceTests`, `WebServiceRequestTests` (12) | Unit / Integration |
| R13 | Round 2, F9: `NestingDepth` counts the levels below `<body>` (its own children at one, n nested divs = n, elements and not text or comment nodes, `<head>` never counted however deep, the deepest chain and not the sibling count, no body = 0, a frameset page measured), is allowed exactly at the limit and refused one past it; end to end, a document nested past 300 is refused naming the URL and the limit while one exactly at it still converts | `WebServiceTests`, `WebServiceScrapeTests` | Unit / Integration |
| R14 | Round 2, F4: `WindowFilter.IsChromium` — the Chromium family with or without `.exe` and in any case, Firefox rejected although `IsBrowser` accepts it, everything else rejected, a null/blank process name false for both, and `ChromiumProcesses` is exactly the browser set minus `firefox` | `WindowFilterTests` (+26 cases) | Unit |

Coverage: `DomPage`, `ScrapeSummary`, `TextCap`, `WebTools` 100 % line and branch;
`WebService.ScrapeAsync` fully covered once the timeout constructor landed. Bite check: the
stateless branch inverted (19 tests across three layers), `Hint` off by one at 100, the
`query` refusal moved above `source`, the surrogate step-back removed — all caught.

## Round 2 — what the review-agent found (2026-09-08)

The REVIEW step reported eight C-5 findings, two of them demonstrated against the built branch;
each went back to `test-agent` as a RED row and was fixed with the family.

| # | Input → wrong outcome | Decision |
|---|---|---|
| F2 | A real page under the 500-element budget: the walk stops mid-page, `Pages[0]` is a partial page, and `Truncated:false` presents it as whole (with `summarize:true` the model then swears the page does not answer a question that was cut off) | `Truncated` is true for either cut; a budget-cut walk carries a `Note` naming the budget and `--max-tree-elements`; `Pages` empty with `Truncated` true is refused with the budget wording, not "is not a browser" |
| F13 | The sampled model is never told the text was cut | `ScrapeSummary.SystemPrompt(query, truncated)` appends "only the beginning of a longer page … do not conclude that the page lacks something that may lie beyond the cut"; `Request` takes `result.Truncated` |
| F4 | Firefox is in the A-1 browser set, so it is picked as "the browser", finds no `RootWebArea`, and comes back as empty `Content` with a note saying the window *was* walked | The frontmost pick is Chromium only (`WindowFilter.IsChromium`: chrome, msedge, brave, opera, vivaldi); Firefox as the only browser is refused by name with `source:http` as the alternative; a page with a note and no document is refused carrying the note — `DomCorrection.NoPage` never has text, so "the text that was walked" was a shape the code cannot produce |
| F3 | `window:"Edge"` with a File Explorer window whose title contains "edge" in front: the substring match walks it first, the budget is spent, `Pages` is empty → "'Edge' is not a browser window" | The page is the first entry in `Pages` with a `DocumentId`; the empty-with-budget case is worded as such. A second resolution by title (the snapshot has no `Hwnd`) is recorded below as a follow-up |
| F5 | `scrape(url:"ftp://…")`, `file:///`, `data:`, `ws:`, `javascript:` → `NotSupportedException`, masked; a scheme with no host resolved `""` — this machine — for the address check | The scheme is decided first, in `ValidateUrlAsync`, for `scrape` and `http_request` alike: anything but http/https is an `ArgumentException` naming the scheme |
| F9 | HTML nested ~900 deep → `ReverseMarkdown` overflows the stack, which no `catch` stops, and the whole server dies mid-call from an `OpenWorld` tool | The parsed tree's depth below `<body>` is measured iteratively (AngleSharp, the converter's own parser) and anything past 300 is refused naming the URL and the limit |
| F10 | A sampling client that never answers hangs the call forever; only `McpException` was caught, so an `InvalidOperationException` or an `IOException` lost the page | `SamplingTimeout` 120 s on a linked token; the clock's expiry and every non-cancellation exception become a `Note`; only the caller's cancellation propagates |
| F11 | `<!-- <title>DRAFT</title> -->`, `<svg><title>Menu icon</title>`, `var t='<title>injected</title>'` — the regex took the first `<title>` anywhere, an injection surface | `IDocument.Title` from AngleSharp: the document's own title as a browser resolves it |
| F12 | `http://alice:s3cr3t@host/page` → the password echoed in `Url` | User info is stripped from the reported `Url`; the query string is kept |

Examined and found clean: the refusal order, blank-vs-null on every string parameter, the
`TwoSentences` join after a quote, the JSON null convention, the C-7 hints, the `TransportOptions`
wiring, `TextCap` and `DomPage` at every boundary, one `HttpClient` per process.

## Deviations and follow-ups

- **Two window resolutions (round 2, F3).** The tool picks the frontmost Chromium window from
  the inventory and then names it to the snapshot by *title*, which the snapshot re-resolves
  (exact, then substring) against an inventory that includes minimised windows. Two windows with
  the same exact title, or a `window:` substring that also matches a non-browser walked first,
  can still yield the wrong page or an exhausted budget. The fix is a `SnapshotRequest` that
  accepts an `Hwnd` — a snapshot contract change for its own item.
- **MCP sampling is deprecated in the SDK.** ModelContextProtocol 2.2.0 marks every sampling
  type obsolete (analyzer `MCP9005`, specification revision 2026-07-28, SEP-2577), which
  `TreatWarningsAsErrors` turns into a build break. There is no replacement that asks the
  *client's* model, and C-5's summary exists only through that route, so the warning is
  suppressed narrowly (`#pragma warning disable MCP9005` with the reason, in
  `Tools/ISamplingClient.cs`, `Tools/WebTools.cs`, `Services/ScrapeSummary.cs` and the five
  test files that touch the types — `WebToolsTests`, `WebToolsDomDesktopTests`,
  `ScrapeSummaryTests`, `HttpTransportTests`, `StreamTransportTests` — the `SYSLIB0057`
  precedent). If a later SDK removes
  `SampleAsync`, `scrape(summarize:true)` loses its summary path and keeps its text path; the
  seam is the one place to re-home it.
- **The end-to-end sampling proof runs over an in-process stream transport, not HTTP.** The
  roadmap's RED seed put it in `HttpTransportTests`; the stateless HTTP host cannot carry it
  (above), so the HTTP test pins the honest fallback and the round trip is proven with
  `StreamServerTransport` / `StreamClientTransport` over pipes — the first test that drives the
  session-keeping path in-process.
- **The SDK client declares sampling from its handler.** ModelContextProtocol 2.2.0 derives the
  client's sampling capability from an installed `SamplingHandler`; a client with a handler and
  `Capabilities.Sampling` left null still initialises as sampling-capable. The "no capability"
  tests therefore use a client with no handler at all — a future reader adding a recording
  handler to prove it is never called will silently invert the test.
- **Pre-existing, not this item:** `HttpClient` follows redirects, and the private-address check
  runs on the URL as given, so a public host that redirects to a private address is fetched.
  Both `scrape` and `http_request` share it. Recorded for a follow-up item.

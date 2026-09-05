// TODO (v0.3.0): Element cache (_elementCache) is unbounded by design in v0.2.0.
// Every ToInfo() call inserts a new entry. LRU eviction tracked for v0.3.0 (Task 22).
// For now, callers should create short-lived UIAutomationService instances per operation
// if memory pressure is a concern.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using Windows.Win32;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

public sealed class UIAutomationService : IUIAutomationService
{
    private readonly UIA3Automation _automation;
    private readonly BlockingCollection<Action> _workQueue = new();
    private readonly Thread _staThread;
    private readonly Dictionary<string, AutomationElement> _elementCache = new();
    private readonly Lock _cacheLock = new();
    private int _nextId;
    private int _disposed;   // 0 = alive, 1 = disposed; treat atomically via Interlocked

    private readonly IInputService _input;
    private readonly ILogger _log;

    /// <param name="input">
    /// Physical input for the two <c>interact_element</c> paths that have no UIA pattern to use:
    /// a click at the element's centre, and keyboard entry when there is no writable ValuePattern.
    /// </param>
    /// <param name="log">
    /// Optional so tests can construct the service directly. The find path swallows per-element and
    /// per-window failures by design (D-5); this is where a failure that is <i>not</i> a recognised
    /// stale-element failure gets recorded, so a new failure mode is visible instead of silent.
    /// </param>
    public UIAutomationService(IInputService input, ILogger<UIAutomationService>? log = null)
    {
        _input = input;
        _log = log ?? (ILogger)NullLogger<UIAutomationService>.Instance;
        _automation = new UIA3Automation();
        _staThread = new Thread(WorkerLoop) { IsBackground = true, Name = "WindowsMcp-UA-STA" };
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.Start();
    }

    private void WorkerLoop()
    {
        foreach (var work in _workQueue.GetConsumingEnumerable())
        {
            try { work(); } catch { /* exceptions are propagated via TaskCompletionSource in each work item */ }
        }
    }

    private Task<T> OnStaAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(UIAutomationService));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Register cancellation: if ct fires while work is still backlogged in the queue,
        // we don't want the caller's await to sit forever.
        var ctRegistration = ct.Register(() => tcs.TrySetCanceled(ct));

        try
        {
            _workQueue.Add(() =>
            {
                try
                {
                    if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                    tcs.TrySetResult(work());
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException)   // CompleteAdding raced with us
        {
            tcs.TrySetException(new ObjectDisposedException(nameof(UIAutomationService)));
        }

        // Dispose registration when task completes — prevents leak if ct outlives task.
        tcs.Task.ContinueWith(_ => ctRegistration.Dispose(), TaskScheduler.Default);
        return tcs.Task;
    }

    public Task<ElementTree> GetStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() => BuildTree(GetForegroundRoot(), depth: 3), ct);
    }

    /// <summary>
    /// The element whose subtree represents "current state": the foreground top-level window
    /// (what an agent actually acts on). Falls back to the focused element, then the desktop.
    /// Rooting at the focused element directly is wrong — a focused leaf control (a text box,
    /// a button) has no children, yielding an empty, useless tree.
    /// </summary>
    private static unsafe nint HwndPointer(Windows.Win32.Foundation.HWND h) => (nint)h.Value;

    private AutomationElement GetForegroundRoot()
    {
        try
        {
            var hwnd = PInvoke.GetForegroundWindow();
            if (!hwnd.IsNull)
            {
                var window = _automation.FromHandle(HwndPointer(hwnd));
                if (window is not null) return window;
            }
        }
        catch { /* fall through to focused element / desktop */ }

        return _automation.FocusedElement() ?? _automation.GetDesktop();
    }

    private ElementTree BuildTree(AutomationElement el, int depth)
    {
        var info = ToInfo(el);
        if (depth <= 0) return new ElementTree(info, Array.Empty<ElementTree>());
        var children = el.FindAllChildren().Select(c => BuildTree(c, depth - 1)).ToArray();
        return new ElementTree(info, children);
    }

    private ElementInfo ToInfo(AutomationElement el)
    {
        string id;
        lock (_cacheLock)
        {
            id = $"el_{_nextId++}";
            _elementCache[id] = el;
        }
        return new ElementInfo(
            ElementId: id,
            Name: TryGetName(el),
            ControlType: TryGetControlType(el),
            IsEnabled: TryGetIsEnabled(el),
            IsOffscreen: TryGetIsOffscreen(el),
            Bounds: TryGetBounds(el),
            Value: TryGetValue(el),
            IsChecked: TryGetChecked(el),
            IsSelected: TryGetSelected(el));
    }

    // A-13: every UI-supplied string goes through UiText.Sanitize before it reaches a DTO.
    private static string TryGetName(AutomationElement el)
    {
        try { return UiText.Sanitize(el.Name); } catch { return ""; }
    }

    private static string TryGetControlType(AutomationElement el)
    {
        try { return el.ControlType.ToString(); } catch { return "Unknown"; }
    }

    private static bool TryGetIsEnabled(AutomationElement el)
    {
        try { return el.IsEnabled; } catch { return false; }
    }

    private static bool TryGetIsOffscreen(AutomationElement el)
    {
        try { return el.IsOffscreen; } catch { return false; }
    }

    // The last unguarded read in ToInfo before D-5: a bounds read on an element that died between
    // the walk and the read used to fail the whole find call.
    private static Bounds? TryGetBounds(AutomationElement el)
    {
        try
        {
            var b = el.BoundingRectangle;
            return new Bounds((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
        }
        catch { return null; }
    }

    private static bool TryIsScrollable(AutomationElement el)
    {
        try { return el.Patterns.Scroll.IsSupported; } catch { return false; }
    }

    private static string? TryGetValue(AutomationElement el)
    {
        try
        {
            var raw = el.Patterns.Value.PatternOrDefault?.Value.Value;
            return raw is null ? null : UiText.Sanitize(raw);
        }
        catch { return null; }
    }

    private static bool? TryGetChecked(AutomationElement el)
    {
        try { return el.Patterns.Toggle.PatternOrDefault?.ToggleState.Value == ToggleState.On; } catch { return null; }
    }

    private static bool? TryGetSelected(AutomationElement el)
    {
        try { return el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.Value; } catch { return null; }
    }

    /// <summary>Result cap. Applied AFTER every filter (D-7) so an on-screen match is never
    /// crowded out by off-screen ones. A real element budget on the walk itself is A-4.</summary>
    private const int MaxMatches = 20;

    /// <summary>
    /// D-6: upstream's <c>INTERACTIVE_CONTROL_TYPE_NAMES</c> (<c>tree/config.py</c>) plus
    /// <see cref="ControlType.Document"/>. Upstream's <c>TextBox</c> is omitted — there is no such
    /// UIA control type; it is <see cref="ControlType.Edit"/>, already here. <c>Document</c> is in
    /// because <c>find_element</c> has one flat kind and a text area you type into is something you
    /// interact with (modern Notepad's editor is a Document, not an Edit). Upstream's
    /// LegacyIAccessible role fallback is deliberately not ported: it costs a second cross-process
    /// read per element, and belongs with A-2's classifier where a cache makes it affordable.
    /// One named set so A-2 can take it over without the two drifting apart.
    /// </summary>
    internal static readonly ControlType[] InteractiveControlTypes =
    [
        ControlType.Button, ControlType.ListItem, ControlType.MenuItem, ControlType.Edit,
        ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.Hyperlink,
        ControlType.SplitButton, ControlType.TabItem, ControlType.TreeItem, ControlType.DataItem,
        ControlType.HeaderItem, ControlType.Spinner, ControlType.Slider, ControlType.ScrollBar,
        ControlType.Document,
    ];

    public Task<FindElementResult> FindElementAsync(string text, FindKind kind = FindKind.Any,
        FindScope scope = FindScope.Foreground, string? windowTitle = null,
        bool includeOffscreen = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (scope == FindScope.Window && string.IsNullOrWhiteSpace(windowTitle))
            throw new ArgumentException("scope=window requires windowTitle: the title of the window to search.", nameof(windowTitle));
        if (scope != FindScope.Window && windowTitle is not null)
            throw new ArgumentException("windowTitle is only used with scope=window.", nameof(windowTitle));

        return OnStaAsync(() =>
        {
            var hits = new List<ElementInfo>(MaxMatches);
            foreach (var root in RootsFor(scope, windowTitle))
            {
                if (hits.Count >= MaxMatches) break;
                CollectFrom(root, text, kind, includeOffscreen, hits);
            }
            return new FindElementResult(hits.ToArray());
        }, ct);
    }

    /// <summary>
    /// The window root(s) a find walks. One root per window, so a window closing mid-walk drops
    /// that window from the results instead of failing the call (D-5).
    /// </summary>
    private IEnumerable<AutomationElement> RootsFor(FindScope scope, string? windowTitle)
    {
        if (scope == FindScope.Foreground)
        {
            yield return GetForegroundRoot();
            yield break;
        }

        AutomationElement[] windows;
        try { windows = _automation.GetDesktop().FindAllChildren(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSkipped("the desktop's window list", ex);
            yield break;
        }

        if (scope == FindScope.Desktop)
        {
            foreach (var w in windows) yield return w;
            yield break;
        }

        // FindScope.Window. Matched on the window's own UIA name rather than through WindowService,
        // whose FindWindow(null, title) is an exact WHOLE-title match: titles carry volatile
        // decoration ("Untitled - Notepad" gains a leading '*' after one keystroke). B-10's fuzzy
        // matcher replaces the substring step here when it lands. Several windows can legitimately
        // share a title (two Explorer windows) — search all of them rather than refusing.
        var named = windows.Where(w => string.Equals(TryGetName(w), windowTitle, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 0)
            named = windows.Where(w => TryGetName(w).Contains(windowTitle!, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (named.Length == 0)
        {
            // No tool returns a window inventory yet (A-1), so an agent that guessed wrong cannot
            // otherwise discover what IS open. Name them.
            var open = windows.Select(TryGetName).Where(n => n.Length > 0).Distinct().Take(15).ToArray();
            throw new KeyNotFoundException(
                $"No top-level window matching '{windowTitle}'. Open windows: " +
                (open.Length > 0 ? string.Join(", ", open.Select(n => $"'{n}'")) : "(none with a title)"));
        }
        foreach (var w in named) yield return w;
    }

    /// <summary>Collects matches from one window root into <paramref name="hits"/>, stopping at
    /// <see cref="MaxMatches"/>. Neither a failed walk nor a failed element aborts the search.</summary>
    private void CollectFrom(AutomationElement root, string text, FindKind kind, bool includeOffscreen, List<ElementInfo> hits)
    {
        // The root itself is a candidate: with a window-scoped search the window element is not one
        // of its own descendants, and find_element("Notepad") should still find the window. The kind
        // test has to be applied CLIENT-side here — the UIA condition below only filters descendants,
        // so without this every window Pane counted as a match for every kind and filled the cap.
        if (RootMatchesKind(root, kind) && TryEvaluate(root, text, kind, includeOffscreen) is { } rootInfo)
            hits.Add(rootInfo);

        AutomationElement[] candidates;
        try
        {
            // Push the control-type filter into the UIA condition so the provider marshals fewer
            // elements. Name stays client-side: UIA property conditions are exact-match and
            // find_element is documented as a "contains" search.
            var condition = KindCondition(_automation.ConditionFactory, kind);
            candidates = condition is null ? root.FindAllDescendants() : root.FindAllDescendants(condition);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSkipped($"window '{TryGetName(root)}'", ex);
            return;
        }

        foreach (var el in candidates)
        {
            if (hits.Count >= MaxMatches) return;
            if (TryEvaluate(el, text, kind, includeOffscreen) is { } info) hits.Add(info);
        }
    }

    /// <summary>
    /// The client-side twin of <see cref="KindCondition"/>, for the one element the UIA condition
    /// cannot filter: the walk's own root. Scrollable is decided by pattern in
    /// <see cref="TryEvaluate"/>, so it passes here.
    /// </summary>
    private static bool RootMatchesKind(AutomationElement el, FindKind kind) => kind switch
    {
        FindKind.Text => TryGetControlTypeEnum(el) is ControlType.Text or ControlType.Edit or ControlType.Document,
        FindKind.Interactive => Array.IndexOf(InteractiveControlTypes, TryGetControlTypeEnum(el)) >= 0,
        _ => true,
    };

    private static ControlType TryGetControlTypeEnum(AutomationElement el)
    {
        try { return el.ControlType; } catch { return ControlType.Unknown; }
    }

    private static ConditionBase? KindCondition(ConditionFactory cf, FindKind kind) => kind switch
    {
        FindKind.Text => new OrCondition(
            cf.ByControlType(ControlType.Text), cf.ByControlType(ControlType.Edit), cf.ByControlType(ControlType.Document)),
        FindKind.Interactive => new OrCondition(InteractiveControlTypes.Select(t => (ConditionBase)cf.ByControlType(t))),
        // Any matches everything; Scrollable is a pattern test, not a property, so it stays
        // client-side in TryEvaluate. Both walk unconditioned.
        _ => null,
    };

    /// <summary>
    /// Decides whether one element is a match, and turns it into an <see cref="ElementInfo"/>.
    /// EVERY read here is guarded, and the whole method is wrapped: a desktop always contains
    /// elements that are about to die (a fading tooltip, a closing menu, a virtualised row), and
    /// before D-5 any one of them failed the entire call. Catching broadly is deliberate — a
    /// provider can raise PropertyNotSupportedException or a bare COMException as well as
    /// UIA_E_ELEMENTNOTAVAILABLE, and none of that is worth failing a search over.
    /// </summary>
    private ElementInfo? TryEvaluate(AutomationElement el, string text, FindKind kind, bool includeOffscreen)
    {
        try
        {
            // Cheapest discriminating test first: one Name read, and it cuts hardest when the
            // caller passed text. Visibility costs two reads, so it runs on the survivors.
            if (text.Length > 0 && !TryGetName(el).Contains(text, StringComparison.OrdinalIgnoreCase)) return null;
            if (kind == FindKind.Scrollable && !TryIsScrollable(el)) return null;
            if (!includeOffscreen && !IsVisibleEnough(el)) return null;
            return ToInfo(el);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// D-7: upstream's <c>is_visible = area &gt; 0 and not is_offscreen</c>, with upstream's
    /// <c>Edit</c> exception — Chromium/WebView2 and some XAML providers report IsOffscreen on edit
    /// fields that are merely scrolled in a container but are still the right target for
    /// <c>type</c>. Real bounds are the guard that keeps destroyed controls out.
    /// Note this is NOT a sign test on X/Y: a monitor left of or above the primary has negative
    /// bounds and is perfectly on screen (D-3).
    /// </summary>
    private static bool IsVisibleEnough(AutomationElement el)
    {
        var b = TryGetBounds(el);
        if (b is null || b.Width <= 0 || b.Height <= 0) return false;
        if (!TryGetIsOffscreen(el)) return true;
        return TryGetControlType(el) == nameof(ControlType.Edit);
    }

    private void LogSkipped(string what, Exception ex)
    {
        // A stale element is the expected case and stays quiet; anything else is a failure mode we
        // have not seen, and swallowing it silently is how a real bug hides behind D-5's guards.
        if (IsElementGone(ex)) _log.LogDebug(ex, "find: skipped {What} (no longer available)", what);
        else _log.LogWarning(ex, "find: skipped {What} after an unexpected failure", what);
    }

    public Task<ElementInfo> GetElementAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            AutomationElement el;
            lock (_cacheLock)
            {
                if (!_elementCache.TryGetValue(elementId, out el!))
                    throw new KeyNotFoundException($"Element '{elementId}' not in cache");
            }
            return ToInfo(el);
        }, ct);
    }

    public Task<string> GetTextAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var el = ResolveCached(elementId);
            return UiText.Sanitize(el.Patterns.Value.PatternOrDefault?.Value.Value ?? el.Name);
        }, ct);
    }

    public Task<AssertResult> AssertElementAsync(string elementId, string state, string? expected = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() => AssertOnSta(elementId, state, expected), ct);
    }

    private static readonly string[] AssertStates = ["exists", "enabled", "checked", "value", "visible", "focused"];

    // D-4: every advertised state is implemented, the result says what was observed, and a stale
    // element (its window closed since the id was issued) is a FAIL, never an exception.
    private AssertResult AssertOnSta(string elementId, string state, string? expected)
    {
        var s = state.ToLowerInvariant();
        if (Array.IndexOf(AssertStates, s) < 0)
            throw new ArgumentException($"Unknown assertion state '{state}'; expected exists|enabled|checked|value|visible|focused.", nameof(state));
        if (s == "value" && expected is null)
            throw new ArgumentException("'value' requires expected: the text to compare against.", nameof(expected));
        if (s != "value" && expected is not null)
            throw new ArgumentException("expected is only used with state=value.", nameof(expected));

        AutomationElement? el;
        lock (_cacheLock) _elementCache.TryGetValue(elementId, out el);
        if (el is null)
        {
            // "exists" is the one question where "I don't know that id" is the answer; for every
            // other state an unknown id is a caller bug (ids only come from find_element/get_state).
            if (s == "exists") return new AssertResult(elementId, s, false, "unknown element id");
            throw new KeyNotFoundException($"Element '{elementId}' not in cache");
        }

        AssertResult Result(bool pass, string observed) => new(elementId, s, pass, observed);

        try
        {
            // Liveness probe for every state. Providers that raise UIA_E_ELEMENTNOTAVAILABLE for a
            // destroyed element are handled by the catch below; the Win32 proxy instead keeps
            // answering with defaults (ControlType Pane, empty Name, ProcessId 0), so a ProcessId of
            // 0 is the reliable "gone" signal for a closed HWND.
            if (el.Properties.ProcessId.ValueOrDefault <= 0)
                return Result(false, "element no longer available");

            switch (s)
            {
                case "exists":
                    return Result(true, Describe(el));
                case "enabled":
                    {
                        // Providers may omit optional properties (FlaUI then throws
                        // PropertyNotSupportedException on .Value); use UIA's documented defaults.
                        bool enabled = el.Properties.IsEnabled.TryGetValue(out bool isEnabled) ? isEnabled : true;
                        return Result(enabled, enabled ? "enabled" : "disabled");
                    }
                case "checked":
                    {
                        var toggle = el.Patterns.Toggle.PatternOrDefault;
                        if (toggle is null) return Result(false, $"no TogglePattern on {Describe(el)}");
                        var toggleState = toggle.ToggleState.ValueOrDefault;
                        return Result(toggleState == ToggleState.On, $"toggle state {toggleState}");
                    }
                case "visible":
                    {
                        // IsOffscreen is optional (modern Notepad's document omits it): unsupported
                        // means "not offscreen", and the bounds decide.
                        if (el.Properties.IsOffscreen.ValueOrDefault) return Result(false, "offscreen");
                        var r = el.Properties.BoundingRectangle.ValueOrDefault;
                        if (r.IsEmpty) return Result(false, "empty bounds");
                        return Result(true, $"on screen at ({r.X},{r.Y}) {r.Width}x{r.Height}");
                    }
                case "focused":
                    {
                        if (el.Properties.HasKeyboardFocus.ValueOrDefault) return Result(true, "has keyboard focus");
                        // Some frameworks report focus on the element UIA returns as focused without
                        // setting HasKeyboardFocus on the cached instance; compare identities too.
                        var focused = TryGetFocusedElement();
                        if (focused is not null && SameElement(focused, el)) return Result(true, "has keyboard focus");
                        return Result(false, focused is null ? "nothing has focus" : $"focus is on {Describe(focused)}");
                    }
                case "value":
                    {
                        // Same read as get_text: the ValuePattern value, else the Name.
                        var actual = el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault;
                        var source = "ValuePattern";
                        if (actual is null) { actual = el.Properties.Name.ValueOrDefault ?? ""; source = "Name"; }
                        // A-13: compare and report what find_element/get_text hand the model, not the
                        // raw string — otherwise a value read back from those tools can never match.
                        actual = UiText.Sanitize(actual);
                        return Result(string.Equals(actual, expected, StringComparison.Ordinal), $"value is '{actual}' (from {source})");
                    }
                default:
                    throw new InvalidOperationException($"Unhandled assertion state '{s}'.");
            }
        }
        catch (Exception ex) when (IsElementGone(ex))
        {
            return Result(false, "element no longer available");
        }
    }

    private AutomationElement? TryGetFocusedElement()
    {
        try { return _automation.FocusedElement(); } catch { return null; }
    }

    // CompareElements fails (0x80040201) when either side has gone stale; that is "not the same".
    private static bool SameElement(AutomationElement a, AutomationElement b)
    {
        try { return a.Equals(b); } catch { return false; }
    }

    /// <summary>
    /// True when <paramref name="ex"/> means the element was destroyed after its id was issued:
    /// FlaUI's <see cref="FlaUI.Core.Exceptions.ElementNotAvailableException"/> (its conversion of
    /// UIA_E_ELEMENTNOTAVAILABLE), the raw HRESULT on paths FlaUI does not wrap, or the RPC failures
    /// UIA raises once the owning process has exited. Shared with the find/wait path (checklist D-5).
    /// </summary>
    internal static bool IsElementGone(Exception ex)
    {
        if (ex is FlaUI.Core.Exceptions.ElementNotAvailableException) return true;
        if (ex is not COMException com) return false;
        return com.HResult is
            unchecked((int)0x80040201)   // UIA_E_ELEMENTNOTAVAILABLE
            or unchecked((int)0x80010108)   // RPC_E_DISCONNECTED
            or unchecked((int)0x800706BA)   // RPC_S_SERVER_UNAVAILABLE
            or unchecked((int)0x800706BE);  // RPC_S_CALL_FAILED
    }

    /// <summary>
    /// Outcome of the STA half of <see cref="InteractAsync"/>: the result to report, plus any input
    /// the caller must inject <b>off</b> the STA thread (a blocked SendInput must never stall the UIA
    /// queue). At most one of the pending members is set.
    /// </summary>
    private sealed record InteractStep(InteractResult Result, (int X, int Y)? PendingClick = null, string? PendingText = null);

    public async Task<InteractResult> InteractAsync(string elementId, string action, string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var step = await OnStaAsync(() => InteractOnSta(elementId, action, value), ct).ConfigureAwait(false);

        if (step.PendingClick is { } point)
            await _input.ClickAsync(point.X, point.Y, MouseButton.Left, 1, ct).ConfigureAwait(false);
        else if (step.PendingText is { } text)
            await _input.TypeAsync(text, ct).ConfigureAwait(false);

        return step.Result;
    }

    // D-2: every branch either acts through a UIA pattern or fails naming the pattern and the
    // control — never a silent no-op — and the result says which pattern or fallback fired.
    private InteractStep InteractOnSta(string elementId, string action, string? value)
    {
        var el = ResolveCached(elementId);
        var a = action.ToLowerInvariant();
        InteractResult Done(string method, string? detail = null) => new(elementId, a, method, detail);

        switch (a)
        {
            case "click":
                {
                    if (el.Patterns.Invoke.PatternOrDefault is { } invoke) { invoke.Invoke(); return new(Done("InvokePattern")); }
                    if (el.Patterns.SelectionItem.PatternOrDefault is { } selection) { selection.Select(); return new(Done("SelectionItemPattern")); }
                    if (el.Patterns.Toggle.PatternOrDefault is { } toggle) { toggle.Toggle(); return new(Done("TogglePattern", ToggleDetail(toggle))); }
                    var (x, y) = ClickPoint(el);
                    return new(Done("PhysicalClick", $"({x},{y})"), PendingClick: (x, y));
                }
            case "invoke":
                {
                    var invoke = el.Patterns.Invoke.PatternOrDefault ?? throw NotSupported(el, "InvokePattern");
                    invoke.Invoke();
                    return new(Done("InvokePattern"));
                }
            case "toggle":
                {
                    var toggle = el.Patterns.Toggle.PatternOrDefault ?? throw NotSupported(el, "TogglePattern");
                    toggle.Toggle();
                    return new(Done("TogglePattern", ToggleDetail(toggle)));
                }
            case "select":
                {
                    if (value is null)
                    {
                        var selection = el.Patterns.SelectionItem.PatternOrDefault ?? throw NotSupported(el, "SelectionItemPattern");
                        selection.Select();
                        return new(Done("SelectionItemPattern"));
                    }

                    // With a value the element is a container (combo box, list): open it if it can be
                    // opened, then select the child item by name.
                    el.Patterns.ExpandCollapse.PatternOrDefault?.Expand();
                    var item = el.FindFirstDescendant(cf => cf.ByName(value))
                        ?? throw new KeyNotFoundException($"No item named '{value}' under {Describe(el)}.");
                    if (item.Patterns.SelectionItem.PatternOrDefault is { } itemSelection) { itemSelection.Select(); return new(Done("SelectionItemPattern", $"item '{value}'")); }
                    if (item.Patterns.Invoke.PatternOrDefault is { } itemInvoke) { itemInvoke.Invoke(); return new(Done("InvokePattern", $"item '{value}'")); }
                    throw NotSupported(item, "SelectionItemPattern");
                }
            case "focus":
                el.Focus();
                return new(Done("Focus"));
            case "type":
                {
                    if (value is null) throw new ArgumentException("'type' requires a value: the text to enter.", nameof(value));
                    el.Focus();
                    var valuePattern = el.Patterns.Value.PatternOrDefault;
                    if (valuePattern is not null && !valuePattern.IsReadOnly.ValueOrDefault)
                    {
                        valuePattern.SetValue(value);
                        return new(Done("ValuePattern", "replaced the whole value"));
                    }
                    return new(Done("Keyboard", "typed at the caret"), PendingText: value);
                }
            default:
                throw new ArgumentException($"Unknown interact action '{action}'; expected click|invoke|toggle|select|focus|type.", nameof(action));
        }
    }

    private static (int X, int Y) ClickPoint(AutomationElement el)
    {
        var r = el.BoundingRectangle;
        if (TryGetIsOffscreen(el) || r.IsEmpty)
            throw new InvalidOperationException($"{Describe(el)} supports no Invoke/SelectionItem/Toggle pattern and has no on-screen bounds to click.");
        return (r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private static string? ToggleDetail(FlaUI.Core.Patterns.ITogglePattern toggle)
    {
        try { return $"now {toggle.ToggleState.ValueOrDefault}"; } catch { return null; }
    }

    private static string Describe(AutomationElement el) => $"{TryGetControlType(el)} '{TryGetName(el)}'";

    private static NotSupportedException NotSupported(AutomationElement el, string pattern)
        => new($"{pattern} not supported on {Describe(el)}.");

    public Task<TableData> GetTableAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var el = ResolveCached(elementId);
            var grid = el.Patterns.Grid.PatternOrDefault
                ?? throw new InvalidOperationException("Element doesn't support GridPattern");
            var rows = grid.RowCount.Value;
            var cols = grid.ColumnCount.Value;

            // GridPattern exposes no headers; column headers come from the TablePattern (if the
            // control supports it). Without this the header row was always empty.
            var headers = new string?[cols];
            var table = el.Patterns.Table.PatternOrDefault;
            var headerEls = table?.ColumnHeaders.ValueOrDefault;
            if (headerEls != null)
            {
                for (int c = 0; c < cols && c < headerEls.Length; c++)
                    headers[c] = headerEls[c].Name;
            }

            var data = new string?[rows][];
            for (int r = 0; r < rows; r++)
            {
                data[r] = new string?[cols];
                for (int c = 0; c < cols; c++)
                {
                    var cell = grid.GetItem(r, c);
                    data[r][c] = cell.Name;
                }
            }
            return BuildTable(headers, data);
        }, ct);
    }

    /// <summary>
    /// The projection from raw UIA strings to the <see cref="TableData"/> DTO: every header and
    /// cell sanitised (A-13), a missing header "" rather than null. Separate from the pattern reads
    /// so it is unit-testable on plain strings — a grid cannot be faked headless.
    /// </summary>
    internal static TableData BuildTable(string?[] rawHeaders, string?[][] rawCells)
        => new(
            rawHeaders.Select(UiText.Sanitize).ToArray(),
            rawCells.Select(row => row.Select(UiText.Sanitize).ToArray()).ToArray());

    public Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs,
        FindKind kind = FindKind.Any, FindScope scope = FindScope.Foreground,
        string? windowTitle = null, bool includeOffscreen = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return PollAsync(async token =>
        {
            // Re-resolved every poll, so scope=window doubles as "wait for that app to open": a
            // window that does not exist yet is a failed poll, which PollAsync retries.
            var matches = await FindElementAsync(text, kind, scope, windowTitle, includeOffscreen, token).ConfigureAwait(false);
            return matches.Matches.Length > 0 ? matches.Matches[0] : null;
        }, timeoutMs, intervalMs, ct);
    }

    /// <summary>
    /// The retry loop behind <c>wait_for</c>, separated from UIA so it is unit-testable with a fake
    /// poll delegate. A poll that throws is recorded and retried — absorbing transient failure is
    /// the entire point of a wait, and before D-5 the first one ended it.
    /// </summary>
    /// <remarks>
    /// Polls at least once, so <c>timeout_ms: 0</c> means "check now" rather than "do nothing".
    /// The sleep is clamped to the remaining budget so the call cannot overshoot the deadline by up
    /// to a whole interval, and to a 10 ms floor so <c>interval_ms: 0</c> cannot peg the STA queue.
    /// On the deadline: null when at least one poll ran cleanly and found nothing (the documented
    /// contract), but <see cref="TimeoutException"/> when EVERY poll failed — answering "not found"
    /// when we never managed to look is the defect this method exists to fix.
    /// </remarks>
    internal static async Task<ElementInfo?> PollAsync(
        Func<CancellationToken, Task<ElementInfo?>> poll, int timeoutMs, int intervalMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        Exception? lastFailure = null;
        var anyCleanPoll = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hit = await poll(ct).ConfigureAwait(false);
                anyCleanPoll = true;
                if (hit is not null) return hit;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastFailure = ex; }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var delay = TimeSpan.FromMilliseconds(Math.Max(10, intervalMs));
            if (delay > remaining) delay = remaining;
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        if (!anyCleanPoll && lastFailure is not null)
            throw new TimeoutException(
                $"wait_for: every poll failed within {timeoutMs} ms; last error: {lastFailure.Message}", lastFailure);

        return null;
    }

    public Task FocusAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync<int>(() =>
        {
            var el = ResolveCached(elementId);
            el.Focus();
            return 0;
        }, ct);
    }

    private AutomationElement ResolveCached(string id)
    {
        lock (_cacheLock)
        {
            if (!_elementCache.TryGetValue(id, out var el))
                throw new KeyNotFoundException($"Element '{id}' not in cache");
            return el;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Enqueue the COM teardown so it runs on the STA worker (UIA3Automation
        // holds STA-affine COM references — disposing from MTA can leak or
        // throw RPC_E_WRONG_THREAD on some Windows versions).
        try
        {
            _workQueue.Add(() =>
            {
                try { _automation.Dispose(); }
                catch (Exception) { /* best-effort during shutdown */ }
            });
        }
        catch (InvalidOperationException) { /* queue already completed */ }

        _workQueue.CompleteAdding();

        if (!_staThread.Join(TimeSpan.FromSeconds(2)))
        {
            // Worker hung; leak rather than block server shutdown.
            // (No safe way to abort an STA thread in .NET 9.)
        }

        _workQueue.Dispose();
    }
}

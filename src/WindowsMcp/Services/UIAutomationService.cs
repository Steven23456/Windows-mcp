// TODO (v0.3.0): Element cache (_elementCache) is unbounded by design in v0.2.0.
// Every ToInfo() call inserts a new entry. LRU eviction tracked for v0.3.0 (Task 22).
// For now, callers should create short-lived UIAutomationService instances per operation
// if memory pressure is a concern.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
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

    /// <param name="input">
    /// Physical input for the two <c>interact_element</c> paths that have no UIA pattern to use:
    /// a click at the element's centre, and keyboard entry when there is no writable ValuePattern.
    /// </param>
    public UIAutomationService(IInputService input)
    {
        _input = input;
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
    private AutomationElement GetForegroundRoot()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                var window = _automation.FromHandle(hwnd);
                if (window is not null) return window;
            }
        }
        catch { /* fall through to focused element / desktop */ }

        return _automation.FocusedElement() ?? _automation.GetDesktop();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

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
        var b = el.BoundingRectangle;
        return new ElementInfo(
            ElementId: id,
            Name: TryGetName(el),
            ControlType: TryGetControlType(el),
            IsEnabled: TryGetIsEnabled(el),
            IsOffscreen: TryGetIsOffscreen(el),
            Bounds: new Bounds((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height),
            Value: TryGetValue(el),
            IsChecked: TryGetChecked(el),
            IsSelected: TryGetSelected(el));
    }

    private static string TryGetName(AutomationElement el)
    {
        try { return el.Name ?? ""; } catch { return ""; }
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

    private static string? TryGetValue(AutomationElement el)
    {
        try { return el.Patterns.Value.PatternOrDefault?.Value.Value; } catch { return null; }
    }

    private static bool? TryGetChecked(AutomationElement el)
    {
        try { return el.Patterns.Toggle.PatternOrDefault?.ToggleState.Value == ToggleState.On; } catch { return null; }
    }

    private static bool? TryGetSelected(AutomationElement el)
    {
        try { return el.Patterns.SelectionItem.PatternOrDefault?.IsSelected.Value; } catch { return null; }
    }

    public Task<FindElementResult> FindElementAsync(string text, FindKind kind = FindKind.Any, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var root = _automation.GetDesktop();
            var all = root.FindAllDescendants();
            var matches = all
                .Where(el => MatchesKind(el, kind))
                .Where(el => string.IsNullOrEmpty(text) || (el.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(20)
                .Select(ToInfo)
                .ToArray();
            return new FindElementResult(matches);
        }, ct);
    }

    private static bool MatchesKind(AutomationElement el, FindKind kind) => kind switch
    {
        FindKind.Any => true,
        FindKind.Text => el.ControlType is ControlType.Text or ControlType.Edit or ControlType.Document,
        FindKind.Interactive => el.ControlType is ControlType.Button or ControlType.CheckBox or ControlType.Hyperlink or ControlType.MenuItem,
        FindKind.Scrollable => el.Patterns.Scroll.IsSupported,
        _ => true
    };

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
            return el.Patterns.Value.PatternOrDefault?.Value.Value ?? el.Name ?? "";
        }, ct);
    }

    public Task<bool> AssertElementAsync(string elementId, string state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var el = ResolveCached(elementId);
            return state.ToLowerInvariant() switch
            {
                "exists"  => true,
                "enabled" => el.IsEnabled,
                "checked" => TryGetChecked(el) == true,
                "visible" => !el.IsOffscreen,
                _ => throw new ArgumentException($"Unknown assertion state: '{state}'")
            };
        }, ct);
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
            var headers = new string[cols];
            var table = el.Patterns.Table.PatternOrDefault;
            var headerEls = table?.ColumnHeaders.ValueOrDefault;
            if (headerEls != null)
            {
                for (int c = 0; c < cols && c < headerEls.Length; c++)
                    headers[c] = headerEls[c].Name ?? "";
            }

            var data = new string[rows][];
            for (int r = 0; r < rows; r++)
            {
                data[r] = new string[cols];
                for (int c = 0; c < cols; c++)
                {
                    var cell = grid.GetItem(r, c);
                    data[r][c] = cell.Name ?? "";
                }
            }
            return new TableData(headers, data);
        }, ct);
    }

    public async Task<ElementInfo?> WaitForAsync(string text, int timeoutMs, int intervalMs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var matches = await FindElementAsync(text, FindKind.Any, ct).ConfigureAwait(false);
            if (matches.Matches.Length > 0) return matches.Matches[0];
            await Task.Delay(intervalMs, ct).ConfigureAwait(false);
        }
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

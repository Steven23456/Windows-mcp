// TODO (v0.3.0): Element cache (_elementCache) is unbounded by design in v0.2.0.
// Every ToInfo() call inserts a new entry. LRU eviction tracked for v0.3.0 (Task 22).
// For now, callers should create short-lived UIAutomationService instances per operation
// if memory pressure is a concern.

using System.Collections.Concurrent;
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
    private bool _disposed;

    public UIAutomationService()
    {
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

    private Task<T> OnStaAsync<T>(Func<T> work)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UIAutomationService));
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workQueue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task<ElementTree> GetStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var foreground = _automation.FocusedElement() ?? _automation.GetDesktop();
            return BuildTree(foreground, depth: 3);
        });
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
        });
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
        });
    }

    public Task<string> GetTextAsync(string elementId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync(() =>
        {
            var el = ResolveCached(elementId);
            return el.Patterns.Value.PatternOrDefault?.Value.Value ?? el.Name ?? "";
        });
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
        });
    }

    public Task InteractAsync(string elementId, string action, string? value, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return OnStaAsync<int>(() =>
        {
            var el = ResolveCached(elementId);
            switch (action.ToLowerInvariant())
            {
                case "toggle":
                    el.Patterns.Toggle.PatternOrDefault?.Toggle();
                    break;
                case "select":
                    if (value is null) throw new ArgumentException("'select' requires a value");
                    el.Patterns.SelectionItem.PatternOrDefault?.Select();
                    break;
                case "invoke":
                    el.Patterns.Invoke.PatternOrDefault?.Invoke();
                    break;
                default:
                    throw new ArgumentException($"Unknown interact action: '{action}'");
            }
            return 0;
        });
    }

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
            var headers = new string[cols];
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
        });
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
        });
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
        if (_disposed) return;
        _disposed = true;
        _workQueue.CompleteAdding();
        _staThread.Join(TimeSpan.FromSeconds(2));
        _automation.Dispose();
        _workQueue.Dispose();
    }
}

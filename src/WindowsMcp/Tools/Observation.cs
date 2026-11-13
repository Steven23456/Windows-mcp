using WindowsMcp.Abstractions;
using WindowsMcp.Abstractions.Models;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Tools;

/// <summary>
/// E-5: the one helper behind observe on click, type, key, shortcut, scroll, drag and
/// interact_element. It parses and validates observe and settle_ms before anything is sent, runs
/// the action, waits settle_ms through <see cref="Delay"/>, takes the snapshot and appends its
/// text to the action's result. The action's result always comes back: a snapshot that fails is
/// a note after it, never an error, because the action already happened.
/// </summary>
internal sealed class Observation
{
    /// <summary>Appended to the description of each observing tool that can name an element.</summary>
    internal const string ToolNote =
        " observe: none (default) returns only the result described above. foreground, or window (the top-level window " +
        "the target element belongs to; needs an element_id), waits settle_ms after the action and then appends a blank " +
        "line, a header and a fresh snapshot of that window, with any menu, dropdown, dialog or other popup its process has " +
        "open, to the result, then a 'Popup text' section with what those popups say (a tooltip, a dialog's message: text, " +
        "not controls); that snapshot replaces the element ids (el_N) of every earlier snapshot. window is resolved before " +
        "the action: when the element's window cannot be determined, or its id is unknown, expired or gone, the call is " +
        "refused without acting. If the window closed because of the action, the foreground window is observed instead and " +
        "the header says so. A snapshot that fails afterwards is reported as a note after the result, not an error: the " +
        "action was still performed.";

    /// <summary>The same for key and shortcut, which have no target element.</summary>
    internal const string ToolNoteNoElement =
        " observe: none (default) returns only the result described above. foreground waits settle_ms after the action " +
        "and then appends a blank line, a header and a fresh snapshot of the foreground window, with any menu, dropdown, " +
        "dialog or other popup its process has open, to the result, then a 'Popup text' section with what those popups say " +
        "(a tooltip, a dialog's message: text, not controls); that snapshot replaces the element ids (el_N) of every " +
        "earlier snapshot. observe:window is refused here, because this call has no target element. A snapshot that " +
        "fails afterwards is reported as a note after the result, not an error: the action was still performed.";

    /// <summary>For the two tools that carry the request's cancellation token.</summary>
    internal const string CancelNote =
        " Cancelling the call during the settle wait or the snapshot drops the result, although the action was performed.";

    internal const string ObserveParameter =
        "none (default) | foreground | window: after the action, append a fresh snapshot of the foreground window, or of the window the target element belongs to (needs an element target: element_id, or for drag from_element_id when the destination is a point)";

    internal const string ObserveParameterNoElement =
        "none (default) | foreground: after the action, append a fresh snapshot of the foreground window. window is refused here: this call has no target element";

    internal const string SettleParameter =
        "Milliseconds to wait after the action before the observe snapshot, 0-5000 (default 250); waited only when observe is not none, range-checked always";

    private enum Mode { None, Foreground, Window }

    /// <param name="Hwnd">For <see cref="Mode.Window"/>: the element's top-level window, looked up BEFORE the action.</param>
    private sealed record Plan(Mode Mode, int SettleMs, long Hwnd);

    public Observation(IUIAutomationService uia)
    {
        Uia = uia;
    }

    /// <summary>Where the observation's snapshot (and observe:window's window lookup) comes from.</summary>
    private IUIAutomationService Uia { get; }

    /// <summary>The settle wait after the action; a seam so tests do not sleep.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>
    /// Runs <paramref name="action"/> and, unless observe is none, appends what the screen shows
    /// afterwards. Everything that can be refused about observe is refused first, before the
    /// action resolves a target or sends any input; if the action throws, nothing is observed.
    /// </summary>
    /// <param name="elementId">The element the action targets, or null when it has none (a point, the cursor, the focus).</param>
    public async Task<string> AroundAsync(string observe, int settleMs, string? elementId, CancellationToken ct, Func<Task<string>> action)
    {
        var plan = await PrepareAsync(observe, settleMs, elementId, ct);
        var result = await action();
        return plan.Mode == Mode.None ? result : await AppendAsync(result, plan, ct);
    }

    private async Task<Plan> PrepareAsync(string observe, int settleMs, string? elementId, CancellationToken ct)
    {
        var mode = (observe ?? "").ToLowerInvariant() switch
        {
            "none" => Mode.None,
            "foreground" => Mode.Foreground,
            "window" => Mode.Window,
            _ => throw new ArgumentException($"Unknown observe '{UiRefusals.Clip(observe ?? "")}'; expected none|foreground|window", nameof(observe)),
        };
        if (settleMs is < 0 or > 5000)
            throw new ArgumentException($"settle_ms must be between 0 and 5000, got {settleMs}", "settle_ms");
        if (mode != Mode.Window) return new Plan(mode, settleMs, 0);

        if (string.IsNullOrWhiteSpace(elementId))
            throw new ArgumentException(
                "observe:window observes the window the target element belongs to, and this call has no element_id. Use observe:foreground.", nameof(observe));
        // Now, not after the action: the click may close the dialog the element is in, and a dead
        // element cannot say which window it was part of.
        return new Plan(mode, settleMs, await Uia.GetElementWindowAsync(elementId, ct));
    }

    private async Task<string> AppendAsync(string result, Plan plan, CancellationToken ct)
    {
        if (plan.SettleMs > 0) await Delay(TimeSpan.FromMilliseconds(plan.SettleMs), ct);

        const string NewIds = "a new snapshot: el_N ids from earlier snapshots are no longer valid";
        string header;
        SnapshotResult snapshot;
        try
        {
            if (plan.Mode == Mode.Foreground)
            {
                snapshot = await Uia.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true), ct);
                header = $"Observed the foreground window after {plan.SettleMs} ms ({NewIds})";
            }
            else
            {
                try
                {
                    snapshot = await Uia.SnapshotAsync(new SnapshotRequest(SnapshotScope.Window, WindowHwnd: plan.Hwnd, IncludePopups: true), ct);
                    header = $"Observed the element's window after {plan.SettleMs} ms ({NewIds})";
                }
                catch (KeyNotFoundException)
                {
                    // The action closed it (OK on a dialog). What the caller wants to see next is
                    // whatever came to the front.
                    snapshot = await Uia.SnapshotAsync(new SnapshotRequest(SnapshotScope.Foreground, IncludePopups: true), ct);
                    header = $"Observed after {plan.SettleMs} ms: the element's window is gone, so this is the foreground window instead ({NewIds})";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One line, and nothing a masked fault would not have told the caller anyway.
            string why = ToolErrors.IsCallerFacing(ex)
                ? string.Join(' ', ToolErrors.MessageFor(ex).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                : $"an unexpected fault ({ex.GetType().Name}).";
            return result + "\n\n" +
                   $"Observation failed: {why} The action above was performed, so do not repeat it; call snapshot to see the result.";
        }

        // The renderer's own truncation footer names snapshot's parameters; say whose they are.
        if (snapshot.Truncated)
            header += "; truncated at the element limit, so call snapshot with scope, window or max_elements for the rest";
        var text = result + "\n\n" + header + ":\n" + SnapshotRenderer.Render(snapshot);
        // What the popups say: a tooltip, or a dialog's question, is text, not a row above.
        if (snapshot.PopupText is { Length: > 0 } popupText)
            text += "\n\nPopup text (not controls, no ids):\n" + string.Join('\n', popupText);
        return text;
    }
}

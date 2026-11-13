using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using WindowsMcp.Abstractions;

namespace WindowsMcp.Tools;

/// <summary>
/// E-6: <c>sequence</c> - up to 20 allowlisted UI steps in one call, run in order through the real
/// tool methods, stopping at the first that does not succeed or at a takeover. See
/// docs/design/E-6-sequence.md.
/// </summary>
[McpServerToolType]
public sealed class SequenceTools
{
    public SequenceTools(IInputService input, IClipboardService clipboard, IUIAutomationService uia, IControlIndicator indicator)
    {
        InputTools = new InputTools(input, clipboard, uia);
        UIAutomationTools = new UIAutomationTools(uia);
        Observation = new Observation(uia);
        Indicator = indicator;
    }

    /// <summary>The one InputTools every input step runs through (tests reach its Delay seam).</summary>
    internal InputTools InputTools { get; }

    /// <summary>The one UIAutomationTools interact_element, wait_for and assert_element run through.</summary>
    internal UIAutomationTools UIAutomationTools { get; }

    /// <summary>The sequence's own observation, taken once after the batch (tests reach its Delay seam).</summary>
    internal Observation Observation { get; }

    /// <summary>Read before every step for a pending takeover; never consumed here.</summary>
    internal IControlIndicator Indicator { get; }

    [McpServerTool(Title = "Sequence", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(
        "Run up to 20 UI steps in order in one call (a menu path, a wizard page, a keyboard-driven dialog). steps_json is a JSON array of " +
        "{\"tool\": name, \"args\": {...}} steps (a JSON string holding that array is accepted too); tool is one of click, type, key, " +
        "shortcut, scroll, drag, interact_element, wait, wait_for, assert_element (exact, lower-case; nothing else can run here), and args " +
        "are that tool's own arguments under their own names, with its defaults; args may be omitted. A step cannot carry observe or " +
        "settle_ms: they belong to sequence. The whole array is validated before the first step runs (unknown tool or argument, missing " +
        "required argument, wrong JSON type: no coercion, \"5\" is not 5), so a refused batch has done nothing and can be fixed and resent " +
        "whole. Steps then run in order, each resolving its target when it runs; no snapshot is taken between steps, so an el_N id only " +
        "works for an element that survives the earlier steps: address later steps by coordinates or keyboard, and use wait_for between " +
        "them (a wait_for with text_exists or focused_element takes a snapshot itself, so the el_N ids of earlier snapshots are no " +
        "longer valid after it). The batch stops at the first step that fails (stoppedBy error), wait_for that is not satisfied (stoppedBy wait_for) or " +
        "assert_element that does not pass (stoppedBy assert_element): the steps before it were performed, so the batch is not atomic; do " +
        "not resend them. If the user takes over the mouse and keyboard with the takeover hotkey, the remaining steps are not run " +
        "(stoppedBy takeover, failedIndex null); a step already in progress finishes first. Cancelling the call stops the batch before the " +
        "next step and drops the result, although the steps so far were performed. Returns {count, completed, results:[{index, tool, ok, " +
        "result | error}], failedIndex, error, stoppedBy}: one results entry per step attempted, result being what that tool returns; " +
        "failedIndex, error and stoppedBy are null when every step ran. observe:foreground waits settle_ms after the batch ends (complete " +
        "or stopped early) and appends a blank line, a header and a fresh snapshot of the foreground window with its popups, then any " +
        "'Popup text' section with what those popups say; the snapshot replaces the element ids (el_N) of every earlier snapshot, and " +
        "one that fails is a note after the result, not an error. observe:window is refused: a sequence has no single target element.")]
    public async Task<string> Sequence(
        [Description("JSON array of steps {\"tool\": name, \"args\": {...}}, or a JSON string holding that array; 1-20 steps")] string steps_json,
        [Description("none (default) | foreground: after the batch ends, append a fresh snapshot of the foreground window. window is refused here: a sequence has no single target element")] string observe = "none",
        [Description(Observation.SettleParameter)] int settle_ms = 250,
        CancellationToken ct = default)
    {
        // Every refusal first: nothing below this line can refuse the batch as a whole.
        var steps = SequenceSteps.Parse(steps_json);
        return await Observation.AroundAsync(observe, settle_ms, elementId: null, ct, () => RunAsync(steps, ct));
    }

    private async Task<string> RunAsync(IReadOnlyList<SequenceStep> steps, CancellationToken ct)
    {
        var results = new List<Dictionary<string, object?>>(steps.Count);
        int completed = 0;
        int? failedIndex = null;
        string? error = null, stoppedBy = null;

        // The pending record is one slot that any call's filter consumes (a concurrent snapshot, a
        // second sequence), so "pending" can be emptied by someone else between two steps. The
        // count only grows: a takeover since this reading stops the batch whoever was told of it.
        // (A count that reads lower later is a disposed indicator at shutdown, not a takeover.)
        long? takeoversAtStart = TakeoverCount();

        foreach (var step in steps)
        {
            // Seven of the ten step tools take no token, so the batch itself must stop here: once
            // the caller has cancelled, no further step starts.
            ct.ThrowIfCancellationRequested();

            // S-11: the server, not the model, decides to send the next step, so only the server
            // can honour a takeover made in between. Read, never consumed: the call-tool filter
            // still appends the takeover note to this call's result.
            if (TakeoverPending() || (takeoversAtStart is { } before && TakeoverCount() is { } now && now > before))
            {
                int last = steps.Count - 1;
                stoppedBy = "takeover";
                error = "The user took over the mouse and keyboard; " +
                        (step.Index == last ? $"step {last} was not run." : $"steps {step.Index}..{last} were not run.");
                break;
            }

            var entry = new Dictionary<string, object?> { ["index"] = step.Index, ["tool"] = step.Tool };
            results.Add(entry);
            string result;
            try
            {
                result = await InvokeAsync(step, ct);
            }
            // Cancellation by the caller ends the call. An OperationCanceledException nobody asked
            // for (a timeout inside a service) is a step failure like any other: the steps before
            // it happened, and the caller needs to be told which.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The masking the call-tool filter applies: a sequence leaks nothing a single call would not.
                error = ToolErrors.IsCallerFacing(ex) ? ToolErrors.MessageFor(ex) : $"An unexpected fault ({ex.GetType().Name}).";
                entry["ok"] = false;
                entry["error"] = error;
                failedIndex = step.Index;
                stoppedBy = "error";
                break;
            }

            var embedded = Embed(result);
            string? stop = StopReason(step.Tool, result, embedded);
            entry["ok"] = stop is null;
            entry["result"] = embedded;
            if (stop is not null)
            {
                failedIndex = step.Index;
                stoppedBy = step.Tool;
                error = stop;
                break;
            }
            completed++;
        }

        return JsonSerializer.Serialize(new { count = steps.Count, completed, results, failedIndex, error, stoppedBy });
    }

    /// <summary>A fault reading the flag counts as "no takeover": the indicator can never fail a tool.</summary>
    private bool TakeoverPending()
    {
        try { return Indicator.TakeoverPending; }
        catch { return false; }
    }

    /// <summary>Null when the count cannot be read: then only the pending flag decides.</summary>
    private long? TakeoverCount()
    {
        try { return Indicator.TakeoverCount; }
        catch { return null; }
    }

    /// <summary>Runs the step through the standalone tool's own method, with the sequence's token where it takes one.</summary>
    private Task<string> InvokeAsync(SequenceStep step, CancellationToken ct)
    {
        var parameters = step.Method.GetParameters();
        var arguments = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            arguments[i] = parameters[i].ParameterType == typeof(CancellationToken) ? ct : step.Arguments[parameters[i].Name!];
        }
        object target = step.Method.DeclaringType == typeof(InputTools) ? InputTools : UIAutomationTools;
        return (Task<string>)step.Method.Invoke(target, BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null)!;
    }

    /// <summary>A JSON object or array is embedded as JSON; anything else ("PASS", "pressed tab") as the string.</summary>
    internal static object Embed(string result)
    {
        var trimmed = result.AsSpan().TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return result;
        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return result;
        }
    }

    /// <summary>Why a step that returned still stops the batch, or null when it succeeded.</summary>
    internal static string? StopReason(string tool, string result, object embedded)
    {
        if (tool == "assert_element")
            return result == "PASS" ? null : $"assert_element did not pass: {result}";
        if (tool != "wait_for") return null;

        // Anything but a result that says Satisfied:true is not a satisfied wait.
        if (embedded is JsonElement { ValueKind: JsonValueKind.Object } waited
            && waited.TryGetProperty("Satisfied", out var satisfied) && satisfied.ValueKind == JsonValueKind.True)
            return null;
        string? detail = embedded is JsonElement { ValueKind: JsonValueKind.Object } w
                         && w.TryGetProperty("Detail", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        return string.IsNullOrEmpty(detail) ? "wait_for was not satisfied." : $"wait_for was not satisfied: {detail}";
    }
}

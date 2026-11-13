using System.Reflection;
using System.Text.Json;
using WindowsMcp.Services.UiTree;

namespace WindowsMcp.Tools;

/// <summary>
/// E-6: one validated step of a <c>sequence</c>. <paramref name="Method"/> is the real tool method
/// (<c>InputTools.Click</c>, <c>UIAutomationTools.WaitFor</c>, ...); <paramref name="Arguments"/>
/// holds a value for every one of its parameters except a <see cref="CancellationToken"/>, keyed by
/// the parameter's (wire) name, with the method's own defaults filled in for those the step omitted.
/// </summary>
internal sealed record SequenceStep(int Index, string Tool, MethodInfo Method, IReadOnlyDictionary<string, object?> Arguments);

/// <summary>
/// E-6: the pure part of <c>sequence</c> - parse <c>steps_json</c>, validate every step against the
/// allowlist and the tool method's parameters, bind the arguments. No service access. Every refusal
/// is an <see cref="ArgumentException"/> for <c>steps_json</c>, naming the step as <c>steps_json[i]</c>.
/// </summary>
internal static class SequenceSteps
{
    /// <summary>How many steps one sequence may carry.</summary>
    internal const int MaxSteps = 20;

    private const string Param = "steps_json";

    private const string BadText = "it has a \\u escape that is half of a surrogate pair (not valid UTF-16).";

    /// <summary>
    /// The allowlist, in the order the refusal lists it: wire name → the standalone tool's own
    /// method. Input, UI Automation and wait tools only; never a shell, file, registry, process or
    /// window tool, and not <c>sequence</c> itself.
    /// </summary>
    private static readonly (string Name, MethodInfo Method)[] Allowed =
    [
        ("click", Tool<InputTools>(nameof(InputTools.Click))),
        ("type", Tool<InputTools>(nameof(InputTools.Type))),
        ("key", Tool<InputTools>(nameof(InputTools.Key))),
        ("shortcut", Tool<InputTools>(nameof(InputTools.Shortcut))),
        ("scroll", Tool<InputTools>(nameof(InputTools.Scroll))),
        ("drag", Tool<InputTools>(nameof(InputTools.Drag))),
        ("interact_element", Tool<UIAutomationTools>(nameof(UIAutomationTools.InteractElement))),
        ("wait", Tool<InputTools>(nameof(InputTools.Wait))),
        ("wait_for", Tool<UIAutomationTools>(nameof(UIAutomationTools.WaitFor))),
        ("assert_element", Tool<UIAutomationTools>(nameof(UIAutomationTools.AssertElement))),
    ];

    /// <summary>The allowlisted wire names, comma-separated, for refusals and the tool description.</summary>
    internal static string AllowedNames { get; } = string.Join(", ", Allowed.Select(a => a.Name));

    /// <summary>The observation belongs to the sequence, so a step may not carry these.</summary>
    private static readonly string[] SequenceOwned = ["observe", "settle_ms"];

    private static MethodInfo Tool<T>(string method) =>
        typeof(T).GetMethod(method) ?? throw new InvalidOperationException($"{typeof(T).Name}.{method} is not a public method.");

    internal static IReadOnlyList<SequenceStep> Parse(string stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
            throw Refuse("steps_json is empty; expected a JSON array of {\"tool\": name, \"args\": {...}} steps.");

        using var document = ParseDocument(stepsJson, "steps_json is not valid JSON");
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            // The B-7 convention: a client may stringify the array a second time.
            var inner = Text(root, index: null);
            if (string.IsNullOrWhiteSpace(inner))
                throw Refuse("steps_json is empty; expected a JSON array of {\"tool\": name, \"args\": {...}} steps.");
            using var innerDocument = ParseDocument(inner, "steps_json is a string that does not hold valid JSON");
            return ParseArray(innerDocument.RootElement);
        }
        return ParseArray(root);
    }

    private static JsonDocument ParseDocument(string json, string what)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex) { throw Refuse($"{what}: {ex.Message}"); }
    }

    private static IReadOnlyList<SequenceStep> ParseArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw Refuse($"steps_json must be a JSON array of steps, got {Kind(array)}.");
        int count = array.GetArrayLength();
        if (count == 0)
            throw Refuse("steps_json has no steps; give at least one.");
        if (count > MaxSteps)
            throw Refuse($"steps_json has {count} steps; a sequence runs at most {MaxSteps}. Split it into several calls.");

        var steps = new List<SequenceStep>(count);
        int index = 0;
        foreach (var element in array.EnumerateArray())
        {
            steps.Add(ParseStep(element, index));
            index++;
        }
        return steps;
    }

    private static SequenceStep ParseStep(JsonElement step, int index)
    {
        if (step.ValueKind != JsonValueKind.Object)
            throw RefuseAt(index, $"a step must be an object {{\"tool\": name, \"args\": {{...}}}}, got {Kind(step)}.");

        JsonElement? toolElement = null, argsElement = null;
        foreach (var property in step.EnumerateObject())
        {
            var propertyName = Name(property, index);
            switch (propertyName)
            {
                case "tool" when toolElement is null: toolElement = property.Value; break;
                case "args" when argsElement is null: argsElement = property.Value; break;
                case "tool" or "args":
                    throw RefuseAt(index, $"'{propertyName}' is given twice.");
                default:
                    throw RefuseAt(index, $"unknown step property '{UiRefusals.Clip(propertyName)}'; a step has only 'tool' and 'args'.");
            }
        }

        if (toolElement is not { } tool)
            throw RefuseAt(index, $"the step has no 'tool'; expected one of {AllowedNames}.");
        if (tool.ValueKind != JsonValueKind.String)
            throw RefuseAt(index, $"'tool' must be a string, got {Kind(tool)}; expected one of {AllowedNames}.");
        var name = Text(tool, index)!;
        MethodInfo? method = null;
        foreach (var allowed in Allowed)
        {
            if (string.Equals(allowed.Name, name, StringComparison.Ordinal)) method = allowed.Method;
        }
        if (method is null)
            throw RefuseAt(index, $"tool '{UiRefusals.Clip(name)}' cannot run in a sequence; allowed (exact, lower-case): {AllowedNames}.");

        if (argsElement is { ValueKind: not JsonValueKind.Object } notObject)
            throw RefuseAt(index, $"'args' must be an object of {name}'s arguments, got {Kind(notObject)}.");

        return new SequenceStep(index, name, method, Bind(name, method, argsElement, index));
    }

    private static Dictionary<string, object?> Bind(string tool, MethodInfo method, JsonElement? args, int index)
    {
        var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToArray();
        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (args is { } supplied)
        {
            foreach (var property in supplied.EnumerateObject())
            {
                var argument = Name(property, index);
                if (Array.IndexOf(SequenceOwned, argument) >= 0)
                    throw RefuseAt(index, $"'{argument}' cannot be set on a step: the observation belongs to the sequence, so pass observe and settle_ms to sequence itself.");
                if (!parameters.Any(p => p.Name == argument))
                    throw RefuseAt(index, $"{tool} has no argument '{UiRefusals.Clip(argument)}'; it takes: {string.Join(", ", parameters.Select(p => p.Name).Where(n => Array.IndexOf(SequenceOwned, n) < 0))}.");
                if (!given.TryAdd(argument, property.Value))
                    throw RefuseAt(index, $"argument '{argument}' is given twice.");
            }
        }

        var bound = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (given.TryGetValue(parameter.Name!, out var value))
                bound[parameter.Name!] = Convert(tool, parameter, value, index);
            else if (parameter.HasDefaultValue)
                bound[parameter.Name!] = parameter.DefaultValue;
            else
                throw RefuseAt(index, $"{tool} needs the argument '{parameter.Name}'.");
        }
        return bound;
    }

    /// <summary>One JSON value as the parameter's CLR type. No coercion: "5" is not 5, 2.5 is not an int.</summary>
    private static object? Convert(string tool, ParameterInfo parameter, JsonElement value, int index)
    {
        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        bool nullable = type != parameter.ParameterType
                        || (!type.IsValueType && new NullabilityInfoContext().Create(parameter).WriteState == NullabilityState.Nullable);

        ArgumentException Wrong(string expected) =>
            RefuseAt(index, $"{tool} argument '{parameter.Name}' must be {expected}, got {Kind(value)}.");

        if (value.ValueKind == JsonValueKind.Null)
            return nullable ? null : throw Wrong(Expected(type));

        if (type == typeof(string))
            return value.ValueKind == JsonValueKind.String ? Text(value, index) : throw Wrong(Expected(type));
        if (type == typeof(bool))
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Wrong(Expected(type));
        if (type == typeof(int))
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : throw Wrong(Expected(type));
        if (type == typeof(double))
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double real) ? real : throw Wrong(Expected(type));

        // A parameter type no allowlisted tool has today: refuse rather than guess a conversion.
        throw RefuseAt(index, $"{tool} argument '{parameter.Name}' cannot be set in a sequence.");
    }

    private static string Expected(Type type) =>
        type == typeof(string) ? "a string"
        : type == typeof(bool) ? "true or false"
        : type == typeof(int) ? "a whole number (JSON number, no quotes, no fraction)"
        : "a number (JSON number, no quotes)";

    private static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => $"the number {UiRefusals.Clip(element.GetRawText())}",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    /// <summary>
    /// System.Text.Json parses an escaped lone surrogate ("\ud800") but throws
    /// InvalidOperationException when the name or string holding it is read. Every name and string
    /// is read through these two, so only that fault is refused as unreadable text.
    /// </summary>
    private static string Name(JsonProperty property, int index)
    {
        try { return property.Name; }
        catch (InvalidOperationException) { throw Unreadable(index); }
    }

    private static string? Text(JsonElement element, int? index)
    {
        try { return element.GetString(); }
        catch (InvalidOperationException) { throw Unreadable(index); }
    }

    private static ArgumentException Unreadable(int? index) => index is { } i
        ? RefuseAt(i, $"the step holds a name or string that cannot be read: {BadText}")
        : Refuse($"steps_json is a string that cannot be read: {BadText}");

    private static ArgumentException Refuse(string message) => new(message, Param);

    private static ArgumentException RefuseAt(int index, string message) => new($"steps_json[{index}]: {message}", Param);
}

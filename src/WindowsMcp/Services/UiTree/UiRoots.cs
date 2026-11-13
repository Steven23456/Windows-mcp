namespace WindowsMcp.Services.UiTree;

/// <summary>
/// D-10 R24b: the "first root that can be had" ladder, without UIA so it is testable headless.
/// </summary>
internal static class UiRoots
{
    /// <summary>
    /// The first candidate that answers non-null without throwing. Each candidate is a
    /// cross-process call, so later ones are not asked once one answers. The LAST candidate is the
    /// unconditional fallback: whatever it returns or throws is the answer. A cancellation
    /// propagates from wherever it is raised.
    /// </summary>
    public static T FirstAvailable<T>(params Func<T?>[] candidates) where T : class
        => FirstAvailable(timeoutsPropagate: false, candidates);

    /// <summary>
    /// The same ladder. With <paramref name="timeoutsPropagate"/>, a rung that times out ends it
    /// with that <see cref="TimeoutException"/> instead of falling to the next rung: for an
    /// observation, a hung foreground window must not quietly become the desktop root.
    /// </summary>
    public static T FirstAvailable<T>(bool timeoutsPropagate, params Func<T?>[] candidates) where T : class
    {
        if (candidates is null || candidates.Length == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));

        for (int i = 0; i < candidates.Length - 1; i++)
        {
            try
            {
                if (candidates[i]() is { } found) return found;
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) when (timeoutsPropagate) { throw; }
            catch (Exception) { /* this root cannot be had; try the next */ }
        }

        return candidates[^1]()!;
    }
}

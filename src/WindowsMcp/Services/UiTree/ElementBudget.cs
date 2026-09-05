namespace WindowsMcp.Services.UiTree;

/// <summary>
/// A-4: the element cap a traversal spends as it goes. Upstream's <c>TreeElementBudget</c>: the
/// walk stops when the budget is gone and every rendered block tells the agent so. Not
/// thread-safe by design — a walk runs on the one STA thread.
/// </summary>
internal sealed class ElementBudget
{
    internal ElementBudget(int limit)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "The element budget must be at least 1.");
        Limit = limit;
    }

    internal int Limit { get; }

    /// <summary>Elements admitted so far; never exceeds <see cref="Limit"/>.</summary>
    internal int Count { get; private set; }

    /// <summary>True once an element was refused — the walk stopped early.</summary>
    internal bool Truncated { get; private set; }

    /// <summary>Admit one element. False (and <see cref="Truncated"/> set) once the budget is spent.</summary>
    internal bool TryTake()
    {
        if (Count >= Limit)
        {
            Truncated = true;
            return false;
        }
        Count++;
        return true;
    }

    /// <summary>The sentence appended to every truncated block. <see cref="SnapshotRenderer"/> prints the same text.</summary>
    internal string Note() => NoteFor(Limit);

    internal static string NoteFor(int limit)
        => $"Truncated at {limit} elements. Narrow the view (scope=foreground, or window=<title>) or raise max_elements.";
}

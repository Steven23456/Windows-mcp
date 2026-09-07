using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Services;

/// <summary>
/// C-3: the pure half of the process list — the CPU percentage from two
/// <c>TotalProcessorTime</c> readings, and the sort and cap. The percentage is normalised across
/// every core (a process saturating one of eight cores reads 12.5, as Task Manager shows),
/// clamped to 0–100 and rounded to one decimal; a window that could not be measured reads 0.
/// </summary>
internal static class CpuSample
{
    internal static double Percent(TimeSpan before, TimeSpan after, TimeSpan elapsed, int cores)
    {
        if (elapsed <= TimeSpan.Zero || cores <= 0) return 0;
        var delta = (after - before).TotalSeconds;
        if (delta <= 0) return 0;
        var percent = delta / elapsed.TotalSeconds / cores * 100;
        return Math.Round(Math.Clamp(percent, 0, 100), 1);
    }

    /// <summary>The two numbers descending, names ascending (ordinal, ignore case), ties by pid; a limit of 0 keeps every row.</summary>
    internal static ProcessDto[] SortAndLimit(IEnumerable<ProcessDto> rows, ProcessSort sortBy, int limit)
    {
        IOrderedEnumerable<ProcessDto> ordered = sortBy switch
        {
            ProcessSort.Cpu => rows.OrderByDescending(r => r.CpuPercent).ThenBy(r => r.Pid),
            ProcessSort.Name => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Pid),
            ProcessSort.Pid => rows.OrderBy(r => r.Pid),
            _ => rows.OrderByDescending(r => r.MemoryMb).ThenBy(r => r.Pid),
        };
        IEnumerable<ProcessDto> result = ordered;
        if (limit > 0) result = result.Take(limit);
        return result.ToArray();
    }
}

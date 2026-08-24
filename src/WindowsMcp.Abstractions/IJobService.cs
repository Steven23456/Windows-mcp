using WindowsMcp.Abstractions.Models;

namespace WindowsMcp.Abstractions;

/// <summary>
/// Background PowerShell jobs: fire-and-poll execution for commands too long for a synchronous
/// tool call (installers, DISM, bulk hashes). Jobs run concurrently, outside the foreground
/// PowerShell serialization gate. Unknown ids are forgiving (null / false), never throw.
/// </summary>
public interface IJobService : IDisposable
{
    /// <summary>Starts a job and returns immediately with its running <see cref="JobInfo"/>.</summary>
    Task<JobInfo> StartAsync(string command, CancellationToken ct = default);

    /// <summary>Snapshot of one job; null when the id is unknown or the job was evicted.</summary>
    JobInfo? GetStatus(string id);

    /// <summary>Buffered output of one job; tailChars &gt; 0 returns only the last N chars per stream. Null when unknown.</summary>
    JobOutput? GetOutput(string id, int tailChars = 0);

    /// <summary>Kills a running job's process tree. False when the id is unknown or the job already finished.</summary>
    bool Cancel(string id);

    /// <summary>Snapshot of all retained jobs, running and finished.</summary>
    JobInfo[] List();
}

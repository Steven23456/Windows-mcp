namespace WindowsMcp.Abstractions.Models;

/// <summary>
/// A background PowerShell job. State is one of: running | completed | failed | timedOut | cancelled.
/// CommandPreview is the first ~120 chars of the command. StdoutTrimmedChars/StderrTrimmedChars
/// count output chars dropped (oldest-first) when a stream exceeded its bounded buffer.
/// </summary>
public record JobInfo(
    string Id,
    string State,
    int Pid,
    string CommandPreview,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    int? ExitCode,
    long StdoutChars,
    long StderrChars,
    long StdoutTrimmedChars,
    long StderrTrimmedChars);

/// <summary>
/// The buffered output of a background job. Stdout/Stderr are the retained tails of each stream
/// (oldest chars are trimmed once the per-stream cap is exceeded); ExitCode is null while running.
/// </summary>
public record JobOutput(
    string Id,
    string State,
    string Stdout,
    string Stderr,
    int? ExitCode,
    long StdoutTrimmedChars,
    long StderrTrimmedChars);

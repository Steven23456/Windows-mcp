namespace WindowsMcp.Abstractions.Models;

/// <param name="TimedOut">
/// C-6: a clock fired — the per-call <c>timeout</c> or the service's execution backstop — the
/// child tree was killed and the output read so far is what is here. Trailing and defaulted, so
/// every construction that predates C-6 keeps compiling and serialises with one extra
/// <c>false</c>. Only the CALLER's cancellation still throws.
/// </param>
/// <param name="StdoutTrimmedChars">
/// F1: chars dropped from the FRONT of <see cref="Stdout"/> to keep it inside the per-stream
/// capacity a job's output already respects (1 000 000 chars, the tail kept). 0 when nothing was
/// dropped. Trailing and defaulted, so every construction that predates F1 keeps compiling.
/// </param>
/// <param name="StderrTrimmedChars">The same count for <see cref="Stderr"/>.</param>
public record PSResult(
    bool Success,
    string Stdout,
    string Stderr,
    int ExitCode,
    string[] Errors,
    bool TimedOut = false,
    long StdoutTrimmedChars = 0,
    long StderrTrimmedChars = 0);

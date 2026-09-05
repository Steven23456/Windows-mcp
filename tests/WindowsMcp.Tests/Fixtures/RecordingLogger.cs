using Microsoft.Extensions.Logging;

namespace WindowsMcp.Tests.Fixtures;

/// <summary>
/// A-14 (R4): an <see cref="ILogger{TCategoryName}"/> that keeps what was written, so a test can
/// assert that the stage timings actually reached the log. The host sends every log line to
/// stderr at Information and above (<c>WindowsMcpHost.ConfigureStderrLogging</c>) — a profiled run
/// that computes the timings and never logs them is exactly the failure this catches, and a
/// <c>NullLogger</c> would hide it.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _records = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Records
    {
        get { lock (_records) return _records.ToArray(); }
    }

    /// <summary>The rendered messages logged at <paramref name="level"/>, in order.</summary>
    public IReadOnlyList<string> MessagesAt(LogLevel level) =>
        Records.Where(r => r.Level == level).Select(r => r.Message).ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    // Everything is enabled: the point is to see what the code chose to write.
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_records) _records.Add((logLevel, formatter(state, exception)));
    }
}

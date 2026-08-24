using System.Text;

namespace WindowsMcp.Services;

/// <summary>
/// Thread-safe bounded text accumulator. When appending would exceed the capacity, the OLDEST
/// chars are trimmed from the front (and <see cref="TrimmedChars"/> counts them) so a chatty
/// child process can never grow a job's output unbounded — the retained text is always the most
/// recent tail. Pure and deterministic — the unit-testable core of the job service's stream capture.
/// </summary>
public sealed class BoundedTextBuffer
{
    private readonly int _capacity;
    private readonly StringBuilder _sb = new();
    private readonly object _lock = new();

    public BoundedTextBuffer(int capacityChars = 1_000_000) => _capacity = Math.Max(1, capacityChars);

    /// <summary>Total chars trimmed from the front to stay under capacity.</summary>
    public long TrimmedChars { get; private set; }

    /// <summary>Chars currently retained.</summary>
    public long Length
    {
        get { lock (_lock) { return _sb.Length; } }
    }

    public void Append(ReadOnlySpan<char> chars)
    {
        lock (_lock)
        {
            // A single write larger than the whole buffer keeps only its tail.
            if (chars.Length > _capacity)
            {
                TrimmedChars += chars.Length - _capacity;
                chars = chars[^_capacity..];
            }
            _sb.Append(chars);
            if (_sb.Length > _capacity)
            {
                int excess = _sb.Length - _capacity;
                _sb.Remove(0, excess);
                TrimmedChars += excess;
            }
        }
    }

    /// <summary>The full retained text.</summary>
    public string Snapshot()
    {
        lock (_lock) { return _sb.ToString(); }
    }

    /// <summary>The last <paramref name="chars"/> retained chars (everything if chars &lt;= 0 or larger than retained).</summary>
    public string Tail(int chars)
    {
        lock (_lock)
        {
            if (chars <= 0 || chars >= _sb.Length) return _sb.ToString();
            return _sb.ToString(_sb.Length - chars, chars);
        }
    }
}

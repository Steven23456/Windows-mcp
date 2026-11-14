using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using WindowsMcp.Services;
using Xunit;

namespace WindowsMcp.Tests.Services;

/// <summary>
/// PR #47 finding C, the part a service test cannot seam: a file that grows WHILE it is being
/// scanned. <see cref="CappedReadStream"/> ends a read after <c>cap</c> bytes and says, through
/// <see cref="CappedReadStream.Exceeded"/>, that the source had more. Pure: in-memory sources only.
/// </summary>
[Trait("Category", "Unit")]
public class CappedReadStreamTests
{
    private static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)('a' + i % 26)).ToArray();

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void A_source_under_the_cap_is_read_whole_and_not_exceeded()
    {
        var source = Bytes(10);
        var capped = new CappedReadStream(new MemoryStream(source), cap: 100);

        ReadAll(capped).Should().Equal(source);
        capped.Exceeded.Should().BeFalse();
    }

    /// <summary>Exactly the cap is not over it: everything is returned and Exceeded stays false.</summary>
    [Fact]
    public void A_source_of_exactly_the_cap_is_read_whole_and_not_exceeded()
    {
        var source = Bytes(100);
        var capped = new CappedReadStream(new MemoryStream(source), cap: 100);

        ReadAll(capped).Should().Equal(source);
        capped.Exceeded.Should().BeFalse("a source of exactly cap bytes has nothing more");
    }

    /// <summary>One byte over: exactly cap bytes come back - the first ones - and Exceeded is true.</summary>
    [Fact]
    public void A_source_one_byte_over_the_cap_returns_exactly_cap_bytes_and_is_exceeded()
    {
        var source = Bytes(101);
        var capped = new CappedReadStream(new MemoryStream(source), cap: 100);

        ReadAll(capped).Should().Equal(source.Take(100));
        capped.Exceeded.Should().BeTrue();
    }

    /// <summary>Large reads do not slip past the cap in one call: the buffer is bigger than what is left.</summary>
    [Fact]
    public void A_single_large_read_stops_at_the_cap()
    {
        var capped = new CappedReadStream(new MemoryStream(Bytes(1000)), cap: 300);
        var buffer = new byte[4096];

        capped.Read(buffer).Should().Be(300);
        capped.Read(buffer).Should().Be(0, "the cap has been reached");
        capped.Exceeded.Should().BeTrue();
        capped.Position.Should().Be(300, "Position is the number of bytes returned");
    }

    [Fact]
    public void A_cap_of_zero_returns_nothing_and_a_non_empty_source_is_exceeded()
    {
        var capped = new CappedReadStream(new MemoryStream(Bytes(5)), cap: 0);

        ReadAll(capped).Should().BeEmpty();
        capped.Exceeded.Should().BeTrue();
    }

    /// <summary>
    /// The case the wrapper exists for: the source grows between reads. It is at end of file, then
    /// is appended to past the cap: the next reads return up to the cap, and the read after that
    /// finds the extra and sets Exceeded.
    /// </summary>
    [Fact]
    public void A_source_that_grows_past_the_cap_between_reads_is_exceeded()
    {
        var source = new GrowingStream(Bytes(5));
        var capped = new CappedReadStream(source, cap: 10);
        var buffer = new byte[64];

        capped.Read(buffer).Should().Be(5);
        capped.Read(buffer).Should().Be(0, "end of file for now");
        capped.Exceeded.Should().BeFalse();

        source.Append(Bytes(20));

        capped.Read(buffer).Should().Be(5, "only up to the cap");
        capped.Read(buffer).Should().Be(0);
        capped.Exceeded.Should().BeTrue("the source now has more than the cap");
    }

    [Fact]
    public void A_source_that_grows_to_exactly_the_cap_between_reads_is_not_exceeded()
    {
        var source = new GrowingStream(Bytes(5));
        var capped = new CappedReadStream(source, cap: 10);
        var buffer = new byte[64];

        capped.Read(buffer).Should().Be(5);
        source.Append(Bytes(5));
        capped.Read(buffer).Should().Be(5);
        capped.Read(buffer).Should().Be(0);
        capped.Exceeded.Should().BeFalse();
    }

    /// <summary>An empty buffer is a no-op: 0, no read from the source, and no Exceeded probe even at the cap.</summary>
    [Fact]
    public void An_empty_buffer_reads_nothing_from_the_source()
    {
        var source = new GrowingStream(Bytes(20));
        var capped = new CappedReadStream(source, cap: 10);

        capped.Read(Span<byte>.Empty).Should().Be(0);
        source.Reads.Should().Be(0);

        capped.Read(new byte[10]).Should().Be(10);
        var readsAtCap = source.Reads;
        capped.Read(Span<byte>.Empty).Should().Be(0);
        source.Reads.Should().Be(readsAtCap, "an empty read at the cap does not probe the source");
        capped.Exceeded.Should().BeFalse("nothing has been asked for past the cap yet");
    }

    /// <summary>Once Exceeded is known, further reads return 0 without probing the source again.</summary>
    [Fact]
    public void Reads_after_the_cap_is_exceeded_do_not_probe_the_source_again()
    {
        var source = new GrowingStream(Bytes(20));
        var capped = new CappedReadStream(source, cap: 10);
        var buffer = new byte[64];

        capped.Read(buffer).Should().Be(10);
        capped.Read(buffer).Should().Be(0);
        capped.Exceeded.Should().BeTrue();
        var reads = source.Reads;

        capped.Read(buffer).Should().Be(0);
        capped.Read(buffer).Should().Be(0);
        source.Reads.Should().Be(reads, "the answer is already known; nothing more is taken from the source");
        capped.Exceeded.Should().BeTrue();
    }

    /// <summary>The array overload honours offset and count, and the cap within them.</summary>
    [Fact]
    public void The_array_overload_honours_offset_count_and_the_cap()
    {
        var capped = new CappedReadStream(new MemoryStream(Bytes(50)), cap: 4);
        var buffer = new byte[20];

        capped.Read(buffer, 5, 10).Should().Be(4);

        buffer.Take(5).Should().OnlyContain(b => b == 0, "nothing before the offset is written");
        buffer.Skip(5).Take(4).Should().Equal(Bytes(4));
        buffer.Skip(9).Should().OnlyContain(b => b == 0, "nothing past what was read is written");
    }

    [Fact]
    public void ReadByte_stops_at_the_cap()
    {
        var capped = new CappedReadStream(new MemoryStream(Bytes(3)), cap: 2);

        capped.ReadByte().Should().Be('a');
        capped.ReadByte().Should().Be('b');
        capped.ReadByte().Should().Be(-1);
        capped.Exceeded.Should().BeTrue();
    }

    [Fact]
    public void It_is_a_read_only_forward_only_stream()
    {
        var capped = new CappedReadStream(new MemoryStream(Bytes(5)), cap: 10);

        capped.CanRead.Should().BeTrue();
        capped.CanSeek.Should().BeFalse();
        capped.CanWrite.Should().BeFalse();
        capped.Invoking(s => s.Length).Should().Throw<InvalidOperationException>();
        capped.Invoking(s => s.Position = 1).Should().Throw<InvalidOperationException>();
        capped.Invoking(s => s.Seek(0, SeekOrigin.Begin)).Should().Throw<InvalidOperationException>();
        capped.Invoking(s => s.SetLength(1)).Should().Throw<InvalidOperationException>();
        capped.Invoking(s => s.Write(new byte[1], 0, 1)).Should().Throw<InvalidOperationException>();
        capped.Invoking(s => s.Flush()).Should().NotThrow("Flush on a read-only stream is a no-op");
    }

    /// <summary>"does not own inner": disposing the wrapper leaves the source open.</summary>
    [Fact]
    public void Disposing_the_wrapper_leaves_the_source_open()
    {
        var source = new MemoryStream(Bytes(5));

        new CappedReadStream(source, cap: 10).Dispose();

        source.CanRead.Should().BeTrue();
        source.ReadByte().Should().Be('a');
    }

    /// <summary>
    /// Through the reader the content search uses: a match beyond the cap is never seen, and the
    /// wrapper reports that the file had more - the service then skips the file.
    /// </summary>
    [Fact]
    public void A_scan_through_the_wrapper_never_sees_a_match_past_the_cap()
    {
        var text = string.Concat(Enumerable.Repeat("line\n", 20)) + "needle past the cap\n";   // 100 bytes, then the match
        var capped = new CappedReadStream(new MemoryStream(Encoding.UTF8.GetBytes(text)), cap: 100);
        using var reader = new StreamReader(capped, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var scan = ContentSearch.Scan(reader, "C:/data/app.log", new Regex("needle", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)), 0, 100);

        scan.Matches.Should().BeEmpty();
        capped.Exceeded.Should().BeTrue();
    }

    /// <summary>A forward-only in-memory source that can be appended to between reads, counting its reads.</summary>
    private sealed class GrowingStream(byte[] initial) : Stream
    {
        private readonly List<byte> _data = [.. initial];
        private int _position;

        public int Reads { get; private set; }

        public void Append(byte[] more) => _data.AddRange(more);

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            int n = Math.Min(buffer.Length, _data.Count - _position);
            for (int i = 0; i < n; i++) buffer[i] = _data[_position + i];
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

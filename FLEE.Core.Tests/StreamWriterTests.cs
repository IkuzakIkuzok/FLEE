using Xunit;

namespace FLEE.Core.Tests;

public sealed class StreamWriterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "FLEE.Tests", Guid.NewGuid().ToString("N"));
    public StreamWriterTests() => Directory.CreateDirectory(this.root);
    public void Dispose() => Directory.Delete(this.root, true);

    [Theory]
    [InlineData(2)] [InlineData(1021)] [InlineData(1022)] [InlineData(16384)]
    public void FileStreamMatchesPathWriterAndRemainsOpen(int count)
    {
        var values = Enumerable.Range(0, count).Select(i => (double)i).ToArray();
        var options = new DasWriteOptions { Weighting = SignalWeighting.Poisson };
        var expectedDirectory = Directory.CreateDirectory(Path.Combine(this.root, "expected"));
        var path = Path.Combine(this.root, "data.das");
        var expectedPath = Path.Combine(expectedDirectory.FullName, "data.das");
        var expectedInfo = DasWriter.Write(expectedPath, values, values, options, values);
        using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var info = DasWriter.Write(destination, values, values, options, values);
            Assert.True(destination.CanWrite);
            Assert.Equal(new(expectedInfo.Channels, expectedInfo.ChannelWidthSeconds, expectedInfo.FirstTimeSeconds,
                expectedInfo.HasPrompt, expectedInfo.Resampled), info);
        }
        Assert.Equal(File.ReadAllBytes(expectedPath), File.ReadAllBytes(path));
    }

    [Fact]
    public void ForwardOnlyStreamNeedsNoSeekFlushOrClose()
    {
        var expectedPath = Path.Combine(this.root, "expected.das");
        var options = new DasWriteOptions { Name = "Dataset", Resample = 4 };
        DasWriter.Write(expectedPath, [0, 1, 3], [0, 10, 0], options, [0, 6, 0], [0, 1.5, 3]);
        using var destination = new ForwardOnlyStream();
        var info = DasWriter.Write(destination, [0, 1, 3], [0, 10, 0],
            options with { Name = null }, [0, 6, 0], [0, 1.5, 3]);
        Assert.Equal(File.ReadAllBytes(expectedPath), destination.ToArray());
        Assert.True(info.HasPrompt);
        Assert.True(info.Resampled);
        Assert.False(destination.Disposed);
        Assert.Equal(0, destination.FlushCount);
        Assert.True(destination.WriteCount > 1);
        Assert.True(destination.LargestWrite < destination.WrittenBytes);
    }

    [Fact]
    public void StreamWritesAtCurrentPositionWithoutTruncating()
    {
        var expectedPath = Path.Combine(this.root, "expected.das");
        var options = new DasWriteOptions { Name = "Embedded" };
        DasWriter.Write(expectedPath, [0, 1], [1, 2], options);
        var expected = File.ReadAllBytes(expectedPath);
        byte[] prefix = [10, 20, 30], suffix = [40, 50];
        using var destination = new MemoryStream();
        destination.Write(prefix);
        destination.Write(new byte[expected.Length]);
        destination.Write(suffix);
        destination.Position = prefix.Length;
        DasWriter.Write(destination, [0, 1], [1, 2], options with { Overwrite = true });
        Assert.Equal(prefix.Length + expected.Length, destination.Position);
        var actual = destination.ToArray();
        Assert.Equal(prefix, actual[..prefix.Length]);
        Assert.Equal(expected, actual[prefix.Length..^suffix.Length]);
        Assert.Equal(suffix, actual[^suffix.Length..]);
    }

    [Theory]
    [MemberData(nameof(ConversionTests.InvalidTraces), MemberType = typeof(ConversionTests))]
    public void InvalidInputDoesNotChangeStream(double[] time, double[] signal, DasWriteOptions options)
    {
        using var destination = new MemoryStream();
        destination.Write([1, 2, 3, 4, 5]);
        destination.Position = 2;
        Assert.ThrowsAny<ArgumentException>(() => DasWriter.Write(destination, time, signal, options));
        Assert.Equal(2, destination.Position);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, destination.ToArray());
        Assert.True(destination.CanWrite);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InvalidMetadataIsRejectedBeforeFirstWrite(bool oversized)
    {
        using var destination = new ForwardOnlyStream();
        var options = new DasWriteOptions { Comment = oversized ? new string('x', 4_000_000) : "\uD800" };
        Assert.ThrowsAny<ArgumentException>(() => DasWriter.Write(destination, [0, 1], [1, 2], options));
        Assert.Equal(0, destination.WriteCount);
        Assert.Equal(0, destination.FlushCount);
        Assert.False(destination.Disposed);
    }

    [Fact]
    public void NullReadOnlyAndClosedStreamsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => DasWriter.Write((Stream)null!, [0, 1], [1, 2]));
        using var readOnly = new MemoryStream([1, 2, 3], writable: false);
        Assert.Throws<ArgumentException>(() => DasWriter.Write(readOnly, [0, 1], [1, 2]));
        Assert.Equal(0, readOnly.Position);
        Assert.Equal(new byte[] { 1, 2, 3 }, readOnly.ToArray());
        using var closed = new MemoryStream();
        closed.Close();
        Assert.Throws<ArgumentException>(() => DasWriter.Write(closed, [0, 1], [1, 2]));
    }

    [Fact]
    public void IoFailurePropagatesWithoutDisposingOrFlushingCallerStream()
    {
        using var destination = new ForwardOnlyStream { FailAfterBytes = 512 };
        var exception = Assert.Throws<IOException>(() => DasWriter.Write(destination, [0, 1], [1, 2]));
        Assert.Same(destination.Failure, exception);
        Assert.Equal(512, destination.WrittenBytes);
        Assert.False(destination.Disposed);
        Assert.Equal(0, destination.FlushCount);
    }

    // Fails immediately if the writer tries to access position/length, seek, read, or truncate.
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream contents = new();
        internal long FailAfterBytes { get; init; } = long.MaxValue;
        internal IOException Failure { get; } = new("Simulated output failure.");
        internal bool Disposed { get; private set; }
        internal int FlushCount { get; private set; }
        internal int WriteCount { get; private set; }
        internal int LargestWrite { get; private set; }
        internal long WrittenBytes => this.contents.Length;
        internal byte[] ToArray() => this.contents.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !this.Disposed;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => this.FlushCount++;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (this.contents.Length + buffer.Length > this.FailAfterBytes) throw this.Failure;
            this.contents.Write(buffer);
            this.WriteCount++;
            this.LargestWrite = Math.Max(this.LargestWrite, buffer.Length);
        }
        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
            if (disposing) this.contents.Dispose();
            base.Dispose(disposing);
        }
    }
}

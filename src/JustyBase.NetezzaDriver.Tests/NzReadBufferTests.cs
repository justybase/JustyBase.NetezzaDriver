using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class NzReadBufferTests
{
    [Fact]
    public void Primitives_ReadFromBufferedData_WithoutExtraStreamCalls()
    {
        byte[] payload = [0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04];
        using var ms = new MemoryStream(payload, writable: false);
        var buf = new NzReadBuffer(new CountingStream(ms), size: 64);
        try
        {
            buf.Ensure(payload.Length);
            Assert.Equal(0x01, buf.ReadByte());
            Assert.Equal(0x0002, buf.ReadInt16BigEndian());
            Assert.Equal(0x00000003, buf.ReadInt32BigEndian());
            Assert.Equal(0x0000000000000004L, buf.ReadInt64BigEndian());
            Assert.Equal(0, buf.BytesBuffered);
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public async Task EnsureAsync_BufferedCompletesSynchronously()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        using var ms = new MemoryStream(payload, writable: false);
        var buf = new NzReadBuffer(ms, size: 64);
        try
        {
            buf.Ensure(8);
            var vt = buf.EnsureAsync(4);
            Assert.True(vt.IsCompletedSuccessfully);
            await vt;
            Assert.Equal(1, buf.ReadByte());
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public async Task EnsureAsync_PartialFill_ReadsAcrossChunks()
    {
        byte[] payload = [0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22];
        using var ms = new OneByteAtATimeStream(new MemoryStream(payload, writable: false));
        var buf = new NzReadBuffer(ms, size: 64);
        try
        {
            await buf.EnsureAsync(4);
            Assert.Equal(0xAA, buf.ReadByte());
            Assert.Equal(0xBB, buf.ReadByte());
            await buf.EnsureAsync(4);
            var span = buf.ReadSpan(4).ToArray();
            Assert.Equal(new byte[] { 0xCC, 0xDD, 0x11, 0x22 }, span);
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public void Oversize_DoesNotGrowSharedBuffer()
    {
        byte[] payload = new byte[200];
        new Random(42).NextBytes(payload);
        using var ms = new MemoryStream(payload, writable: false);
        var buf = new NzReadBuffer(ms, size: 64);
        try
        {
            int before = buf.Capacity;
            byte[] rented = buf.RentOversize(200);
            try
            {
                Assert.Equal(payload, rented.AsSpan(0, 200).ToArray());
            }
            finally
            {
                buf.ReturnOversize(rented);
            }
            Assert.Equal(before, buf.Capacity);
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public async Task EnsureAsync_Cancellation_Throws()
    {
        using var ms = new NeverEndingStream();
        var buf = new NzReadBuffer(ms, size: 64);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await buf.EnsureAsync(4, cts.Token));
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public void ConsumedBytes_TracksLogicalReads_NotStreamReadAhead()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        using var ms = new MemoryStream(payload, writable: false);
        var buf = new NzReadBuffer(ms, size: 64);
        try
        {
            buf.ReadByte();
            Assert.Equal(1, buf.ConsumedBytes);
            Assert.Equal(0x02030405, buf.ReadInt32BigEndian());
            Assert.Equal(5, buf.ConsumedBytes);
            buf.Skip(2);
            Assert.Equal(7, buf.ConsumedBytes);
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public void ReadByteOrEof_ReturnsMinusOneAtEnd()
    {
        using var ms = new MemoryStream([0x2A], writable: false);
        var buf = new NzReadBuffer(ms, size: 16);
        try
        {
            Assert.Equal(0x2A, buf.ReadByteOrEof());
            Assert.Equal(-1, buf.ReadByteOrEof());
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public void ReadExactly_DrainsBufferedThenStream()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        using var ms = new MemoryStream(payload, writable: false);
        var buf = new NzReadBuffer(ms, size: 16);
        try
        {
            // Force read-ahead into the buffer, then request past it.
            buf.Ensure(8);
            byte[] destination = new byte[8];
            buf.ReadExactly(destination);
            Assert.Equal(payload, destination);
            Assert.Equal(8, buf.ConsumedBytes);
        }
        finally
        {
            buf.Dispose();
        }
    }

    [Fact]
    public void ReadExactly_OnShortStream_Throws()
    {
        using var ms = new MemoryStream([1, 2], writable: false);
        var buf = new NzReadBuffer(ms, size: 16);
        try
        {
            Assert.Throws<EndOfStreamException>(() =>
            {
                Span<byte> destination = stackalloc byte[4];
                buf.ReadExactly(destination);
            });
        }
        finally
        {
            buf.Dispose();
        }
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        public int ReadCalls;
        public CountingStream(Stream inner) { _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; return _inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { ReadCalls++; return _inner.Read(buffer); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ReadCalls++; return _inner.ReadAsync(buffer, ct); }
        public override long Seek(long o, SeekOrigin org) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class OneByteAtATimeStream : Stream
    {
        private readonly Stream _inner;
        public OneByteAtATimeStream(Stream inner) { _inner = inner; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(1, count));
        public override int Read(Span<byte> buffer) => _inner.Read(buffer.Slice(0, Math.Min(1, buffer.Length)));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            return await _inner.ReadAsync(buffer.Slice(0, Math.Min(1, buffer.Length)), ct);
        }
        public override long Seek(long o, SeekOrigin org) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override int Read(Span<byte> buffer) => 0;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(ct);
            return new ValueTask<int>(Task.FromCanceled<int>(ct));
        }
        public override long Seek(long o, SeekOrigin org) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

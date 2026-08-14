namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class ProtocolCountingStreamTests
{
    [Fact]
    public async Task CountsLogicalBytesReadAcrossSyncAndAsyncReads()
    {
        using var stream = new ProtocolCountingStream(new MemoryStream([1, 2, 3, 4]));

        Assert.Equal(1, stream.ReadByte());
        Assert.Equal(1, stream.BytesRead);

        byte[] syncBuffer = new byte[2];
        Assert.Equal(2, stream.Read(syncBuffer, 0, syncBuffer.Length));
        Assert.Equal(3, stream.BytesRead);

        byte[] asyncBuffer = new byte[1];
        Assert.Equal(1, await stream.ReadAsync(asyncBuffer.AsMemory(), TestContext.Current.CancellationToken));
        Assert.Equal(4, stream.BytesRead);
    }

    [Fact]
    public async Task CountsPartialReadsAndDoesNotCountEofOrZeroLengthReads()
    {
        using var stream = new ProtocolCountingStream(new OneByteAtATimeStream([1, 2, 3, 4]));
        byte[] buffer = new byte[4];

        Assert.Equal(1, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(1, stream.BytesRead);

        Assert.Equal(
            1,
            await stream.ReadAsync(
                buffer.AsMemory(1, buffer.Length - 1),
                TestContext.Current.CancellationToken));
        Assert.Equal(2, stream.BytesRead);

        Assert.Equal(0, stream.Read(buffer, 0, 0));
        Assert.Equal(2, stream.BytesRead);

        Assert.Equal(3, stream.ReadByte());
        Assert.Equal(3, stream.BytesRead);
        Assert.Equal(4, stream.ReadByte());
        Assert.Equal(4, stream.BytesRead);
        Assert.Equal(-1, stream.ReadByte());
        Assert.Equal(4, stream.BytesRead);
    }

    private sealed class OneByteAtATimeStream : Stream
    {
        private readonly byte[] _buffer;
        private int _position;

        public OneByteAtATimeStream(byte[] buffer)
        {
            _buffer = buffer;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _buffer.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + count, buffer.Length);

            if (count == 0 || _position >= _buffer.Length)
            {
                return 0;
            }

            buffer[offset] = _buffer[_position++];
            return 1;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= _buffer.Length)
            {
                return 0;
            }

            buffer[0] = _buffer[_position++];
            return 1;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override int ReadByte()
        {
            return _position >= _buffer.Length ? -1 : _buffer[_position++];
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

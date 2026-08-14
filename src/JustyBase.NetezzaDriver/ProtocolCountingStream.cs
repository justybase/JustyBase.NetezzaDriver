using System.Threading;

namespace JustyBase.NetezzaDriver;

/// <summary>
/// Counts bytes consumed by the protocol parser without changing the buffering
/// stream already selected by the caller.
/// </summary>
internal sealed class ProtocolCountingStream(Stream inner) : Stream
{
    private long _bytesRead;

    internal long BytesRead => Interlocked.Read(ref _bytesRead);

    private int Count(int bytesRead)
    {
        if (bytesRead > 0)
        {
            Interlocked.Add(ref _bytesRead, bytesRead);
        }

        return bytesRead;
    }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Count(inner.Read(buffer, offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        return Count(inner.Read(buffer));
    }

    public override int ReadByte()
    {
        var value = inner.ReadByte();
        if (value >= 0)
        {
            Interlocked.Increment(ref _bytesRead);
        }

        return value;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var bytesRead = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
            .ConfigureAwait(false);
        return Count(bytesRead);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var bytesRead = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Count(bytesRead);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        inner.FlushAsync(cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        inner.Write(buffer, offset, count);

    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
    }
}

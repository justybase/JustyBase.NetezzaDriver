using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace JustyBase.NetezzaDriver;

/// <summary>
/// Npgsql-style read buffer. Buffers protocol bytes to avoid a
/// <see cref="Stream"/> call per primitive. Fast path (<see cref="Ensure"/>/
/// <see cref="EnsureAsync"/> when bytes are already buffered) is synchronous
/// and allocation-free. Oversize payloads use temporary <see cref="ArrayPool{T}"/>
/// buffers instead of permanently growing the connection buffer.
/// </summary>
internal sealed class NzReadBuffer
{
    private readonly Stream _stream;
    private byte[] _buffer;
    private int _readPos;
    private int _filled;
    private long _consumed;

    public NzReadBuffer(Stream stream, int size = 65536)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        _buffer = ArrayPool<byte>.Shared.Rent(size);
        _readPos = 0;
        _filled = 0;
    }

    public int BytesBuffered => _filled - _readPos;
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Logical number of bytes consumed by the protocol parser, independent of
    /// how far the underlying stream has been read ahead. Used for diagnostics.
    /// </summary>
    public long ConsumedBytes => _consumed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Ensure(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            return;
        if (count > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Use ReadOversize for payloads larger than the buffer.");
        if (BytesBuffered >= count)
            return;
        EnsureSlow(count);
    }

    private void EnsureSlow(int count)
    {
        // Compact remaining bytes to front.
        int remaining = BytesBuffered;
        if (remaining > 0 && _readPos > 0)
        {
            _buffer.AsSpan(_readPos, remaining).CopyTo(_buffer.AsSpan(0, remaining));
        }
        _readPos = 0;
        _filled = remaining;

        while (_filled < count)
        {
            int read = _stream.Read(_buffer.AsSpan(_filled));
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of stream while ensuring protocol bytes.");
            _filled += read;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask EnsureAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            return ValueTask.CompletedTask;
        if (count > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Use ReadOversizeAsync for payloads larger than the buffer.");
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);
        if (BytesBuffered >= count)
            return ValueTask.CompletedTask;
        return EnsureSlowAsync(count, cancellationToken);
    }

    private async ValueTask EnsureSlowAsync(int count, CancellationToken cancellationToken)
    {
        int remaining = BytesBuffered;
        if (remaining > 0 && _readPos > 0)
        {
            _buffer.AsSpan(_readPos, remaining).CopyTo(_buffer.AsSpan(0, remaining));
        }
        _readPos = 0;
        _filled = remaining;

        while (_filled < count)
        {
            int read = await _stream.ReadAsync(_buffer.AsMemory(_filled), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of stream while ensuring protocol bytes.");
            _filled += read;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        Ensure(1);
        _consumed++;
        return _buffer[_readPos++];
    }

    /// <summary>
    /// Reads one byte, returning -1 on a clean end of stream instead of throwing.
    /// Matches <see cref="Stream.ReadByte"/> semantics on the underlying stream,
    /// which the backend-response loop historically relied on.
    /// </summary>
    public int ReadByteOrEof()
    {
        if (_readPos < _filled)
        {
            _consumed++;
            return _buffer[_readPos++];
        }

        // Buffer drained: refill in bulk so the following header/payload bytes
        // are buffered together with this one. A clean EOF yields -1.
        _readPos = 0;
        _filled = 0;
        int read = _stream.Read(_buffer.AsSpan(0));
        if (read <= 0)
            return -1;
        _filled = read;
        _consumed++;
        return _buffer[_readPos++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16BigEndian()
    {
        return ReadInt16BigEndian(out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16BigEndian(out ushort raw)
    {
        Ensure(2);
        raw = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_readPos, 2));
        _readPos += 2;
        _consumed += 2;
        return (short)raw;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32BigEndian()
    {
        return ReadInt32BigEndian(out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32BigEndian(out uint raw)
    {
        Ensure(4);
        raw = BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_readPos, 4));
        _readPos += 4;
        _consumed += 4;
        return (int)raw;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64BigEndian()
    {
        Ensure(8);
        long value = BinaryPrimitives.ReadInt64BigEndian(_buffer.AsSpan(_readPos, 8));
        _readPos += 8;
        _consumed += 8;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadSpan(int count)
    {
        Ensure(count);
        var span = _buffer.AsSpan(_readPos, count);
        _readPos += count;
        _consumed += count;
        return span;
    }

    /// <summary>
    /// Returns a <see cref="ReadOnlyMemory{T}"/> over the next <paramref name="count"/>
    /// bytes and advances the read position. Used by lazy row decoding to retain the
    /// row payload until the next protocol read (the ADO.NET value-lifetime contract).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyMemory<byte> ReadMemory(int count)
    {
        Ensure(count);
        var memory = _buffer.AsMemory(_readPos, count);
        _readPos += count;
        _consumed += count;
        return memory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count)
    {
        Ensure(count);
        _readPos += count;
        _consumed += count;
    }

    /// <summary>
    /// Discards <paramref name="count"/> protocol bytes without decoding them.
    /// Unlike <see cref="Skip"/>, works for payloads larger than the buffer by
    /// consuming them in buffer-sized chunks. Used to drain rows the caller
    /// will never consume (early reader close, SingleRow).
    /// </summary>
    public void Discard(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        while (count > 0)
        {
            int chunk = Math.Min(count, _buffer.Length);
            Ensure(chunk);
            _readPos += chunk;
            _consumed += chunk;
            count -= chunk;
        }
    }

    public async ValueTask DiscardAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        while (count > 0)
        {
            int chunk = Math.Min(count, _buffer.Length);
            await EnsureAsync(chunk, cancellationToken).ConfigureAwait(false);
            _readPos += chunk;
            _consumed += chunk;
            count -= chunk;
        }
    }

    private int DrainBuffered(Span<byte> destination)
    {
        int available = Math.Min(BytesBuffered, destination.Length);
        if (available > 0)
        {
            _buffer.AsSpan(_readPos, available).CopyTo(destination);
            _readPos += available;
        }
        return available;
    }

    /// <summary>
    /// Copies any already-buffered bytes into <paramref name="destination"/>
    /// (up to its length) without touching the underlying stream. Returns the
    /// number of bytes copied. Used by the protocol-sync deadline reader, which
    /// must treat buffered data as immediately available.
    /// </summary>
    public int ReadBuffered(Span<byte> destination)
    {
        int copied = DrainBuffered(destination);
        _consumed += copied;
        return copied;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> exactly, draining the buffer first
    /// and then reading from the stream. Throws <see cref="EndOfStreamException"/>
    /// on premature end of stream.
    /// </summary>
    public void ReadExactly(Span<byte> destination)
    {
        int offset = DrainBuffered(destination);
        while (offset < destination.Length)
        {
            int read = _stream.Read(destination[offset..]);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of stream while reading exact protocol bytes.");
            offset += read;
        }
        _consumed += destination.Length;
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int offset = DrainBuffered(destination.Span);
        while (offset < destination.Length)
        {
            int read = await _stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of stream while reading exact protocol bytes.");
            offset += read;
        }
        _consumed += destination.Length;
    }

    /// <summary>
    /// Reads an oversize payload without growing the shared buffer.
    /// Rents a temporary buffer; caller must return it.
    /// </summary>
    public byte[] RentOversize(int count)
    {
        if (count <= _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Use Ensure for payloads that fit in the buffer.");
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            // Drain any buffered bytes first, then stream the rest.
            int remaining = BytesBuffered;
            if (remaining > 0)
            {
                _buffer.AsSpan(_readPos, remaining).CopyTo(rented.AsSpan(0, remaining));
                _readPos = 0;
                _filled = 0;
            }
            int offset = remaining;
            while (offset < count)
            {
                int read = _stream.Read(rented.AsSpan(offset, count - offset));
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of stream while reading oversize payload.");
                offset += read;
            }
            _consumed += count;
            return rented;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    public async ValueTask<byte[]> RentOversizeAsync(int count, CancellationToken cancellationToken = default)
    {
        if (count <= _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count), "Use Ensure for payloads that fit in the buffer.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] rented = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            int remaining = BytesBuffered;
            if (remaining > 0)
            {
                _buffer.AsSpan(_readPos, remaining).CopyTo(rented.AsSpan(0, remaining));
                _readPos = 0;
                _filled = 0;
            }
            int offset = remaining;
            while (offset < count)
            {
                int read = await _stream.ReadAsync(rented.AsMemory(offset, count - offset), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected end of stream while reading oversize payload.");
                offset += read;
            }
            _consumed += count;
            return rented;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    public void ReturnOversize(byte[] rented) => ArrayPool<byte>.Shared.Return(rented);

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
        _readPos = 0;
        _filled = 0;
    }
}

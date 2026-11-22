using JustyBase.NetezzaDriver.AbortQuery;
using JustyBase.NetezzaDriver.StringPool;
using JustyBase.NetezzaDriver.TypeConvertions;
using JustyBase.NetezzaDriver.Utility;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

// Pooled buffer lifecycle and message-payload reads.

namespace JustyBase.NetezzaDriver;

public sealed partial class NzConnection
{

    private void RegenerateBuffer(int length)
    {
        ValidateProtocolLength(length, "bufferAllocation");
        if (length <= TmpBufferRetainCap && _tmp_buffer.Length < length)
        {
            if (_tmp_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_tmp_buffer);
            }
            _tmp_buffer = ArrayPool<byte>.Shared.Rent(length);
        }
    }

    /// <summary>
    /// Releases the transient buffer used for payloads above
    /// <see cref="TmpBufferRetainCap"/>, plus any retained lazy-row oversize
    /// buffer. Safe to call when no message payload is being processed.
    /// A pooled connection must not pin a multi-megabyte row buffer while idle.
    /// Does not release the persistent <see cref="NzReadBuffer"/>.
    /// </summary>
    internal void ReleaseScratchBuffers()
        => ReleaseTransientBuffers();

    /// <summary>
    /// Unified cleanup for per-query transient memory: large message buffer,
    /// lazy oversize row buffer, and lazy row lifetime. Called when a physical
    /// connection is returned to the pool.
    /// </summary>
    internal void ReleaseTransientBuffers()
    {
        var buffer = _largeReadBuffer;
        if (buffer is not null)
        {
            _largeReadBuffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ReleaseLazyOversize();
        _lazyRowMemory = default;
        _lazyRowActive = false;
    }

    internal bool HasTransientBuffersForTests =>
        _largeReadBuffer is not null || _lazyOversizeBuffer is not null || _lazyRowActive;

    /// <summary>
    /// Drops references owned by the last result set so an idle pooled
    /// connection does not pin result memory (large strings/objects in
    /// <c>RowValue</c>, lazy row payload, statement metadata and per-column
    /// string pools). The reusable <c>RowValue[]</c> buffers themselves are
    /// kept for the next execution; only their element references are
    /// cleared. Safe to call when no read is in flight.
    /// </summary>
    internal void ReleaseResultStateForPooling()
    {
        var row = _row;
        if (row is not null)
            Array.Clear(row);

        ReleaseLazyOversize();
        _lazyRowMemory = default;
        _lazyRowActive = false;

        _nzCommand?.ReleaseResultStateForPooling();
    }

    internal void SetConnectionRowForTests(RowValue[] row) => _row = row;

    internal void SimulateTransientBuffersForTests(int oversizeBytes)
    {
        if (oversizeBytes > 0)
        {
            byte[] lazyRented = System.Buffers.ArrayPool<byte>.Shared.Rent(oversizeBytes);
            _lazyOversizeBuffer = lazyRented;
            _lazyRowMemory = lazyRented.AsMemory(0, oversizeBytes);
            _lazyRowActive = true;

            byte[] largeRented = System.Buffers.ArrayPool<byte>.Shared.Rent(oversizeBytes);
            _largeReadBuffer = largeRented;
        }
    }

    private void ReleaseLargeReadBuffer()
    {
        var buffer = _largeReadBuffer;
        if (buffer is not null)
        {
            _largeReadBuffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a protocol message payload, keeping the shared buffer only for
    /// payloads up to <see cref="TmpBufferRetainCap"/> and using a transient
    /// pooled buffer above that. The returned array is valid until the next
    /// payload read.
    /// </summary>
    private byte[] ReadMessagePayload(int length)
    {
        ValidateProtocolLength(length, "bufferRead");
        ReleaseLargeReadBuffer();
        if (length <= TmpBufferRetainCap)
        {
            if (_tmp_buffer.Length < length)
            {
                if (_tmp_buffer.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(_tmp_buffer);
                }
                _tmp_buffer = ArrayPool<byte>.Shared.Rent(length);
            }
            _readBuffer!.ReadExactly(_tmp_buffer.AsSpan(0, length));
            return _tmp_buffer;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            _readBuffer!.ReadExactly(rented.AsSpan(0, length));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
        _largeReadBuffer = rented;
        return rented;
    }

    private async ValueTask<byte[]> ReadMessagePayloadAsync(int length, CancellationToken cancellationToken)
    {
        ValidateProtocolLength(length, "bufferRead");
        ReleaseLargeReadBuffer();
        if (length <= TmpBufferRetainCap)
        {
            if (_tmp_buffer.Length < length)
            {
                if (_tmp_buffer.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(_tmp_buffer);
                }
                _tmp_buffer = ArrayPool<byte>.Shared.Rent(length);
            }
            await _readBuffer!.ReadExactlyAsync(_tmp_buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            return _tmp_buffer;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await _readBuffer!.ReadExactlyAsync(rented.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
        _largeReadBuffer = rented;
        return rented;
    }

    /// <summary>
    /// Returns a buffer for building an outbound query packet. The shared buffer
    /// is used for commands up to <see cref="TmpBufferRetainCap"/>; larger
    /// commands use a transient pooled buffer that must be passed to
    /// <see cref="ReleaseQueryBuffer"/>.
    /// </summary>
    private byte[] RentQueryBuffer(int length, out bool pooled)
    {
        if (length <= TmpBufferRetainCap)
        {
            pooled = false;
            RegenerateBuffer(length);
            return _tmp_buffer;
        }
        pooled = true;
        return ArrayPool<byte>.Shared.Rent(length);
    }

    private static void ReleaseQueryBuffer(byte[] buffer, bool pooled)
    {
        if (pooled)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

}

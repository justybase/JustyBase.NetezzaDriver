using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace JustyBase.NetezzaDriver.Utility;
//https://github.com/danbarua/Npgsql/blob/master/Npgsql/PGUtil.cs


/// <summary>
/// Provides utility methods for reading and writing from and to streams.
/// </summary>
internal static class PGUtil
{
    /// <summary>
    /// Reads a 32-bit integer from the specified stream.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <returns>The 32-bit integer read from the stream.</returns>
    internal static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        stream.ReadExactly(buffer);
        var result = BitConverter.ToInt32(buffer);
        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(result) : result;
    }

    /// <summary>
    /// Reads a 16-bit integer from the specified stream.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <returns>The 16-bit integer read from the stream.</returns>
    internal static short ReadInt16(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[2];
        stream.ReadExactly(buffer);
        var result = BitConverter.ToInt16(buffer);
        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(result) : result;
    }

    internal static async Task<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            return await ReadInt32Async(stream, buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async ValueTask<int> ReadInt32Async(Stream stream, byte[] scratchBuffer, CancellationToken cancellationToken = default)
    {
        if (scratchBuffer.Length < 4)
        {
            throw new ArgumentException("Scratch buffer must be at least 4 bytes.", nameof(scratchBuffer));
        }

        await stream.ReadExactlyAsync(scratchBuffer.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        var result = BitConverter.ToInt32(scratchBuffer, 0);
        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(result) : result;
    }

    internal static async Task<short> ReadInt16Async(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(2);
        try
        {
            return await ReadInt16Async(stream, buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async ValueTask<short> ReadInt16Async(Stream stream, byte[] scratchBuffer, CancellationToken cancellationToken = default)
    {
        if (scratchBuffer.Length < 2)
        {
            throw new ArgumentException("Scratch buffer must be at least 2 bytes.", nameof(scratchBuffer));
        }

        await stream.ReadExactlyAsync(scratchBuffer.AsMemory(0, 2), cancellationToken).ConfigureAwait(false);
        var result = BitConverter.ToInt16(scratchBuffer, 0);
        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(result) : result;
    }

    /// <summary>
    /// Writes a 16-bit integer to the specified stream.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="value">The 16-bit integer to write.</param>
    internal static void WriteInt16(Stream stream, short value)
    {
        value = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        Unsafe.As<byte, short>(ref bytes[0]) = value;
        stream.Write(bytes);
    }

    /// <summary>
    /// Writes a 32-bit integer to the specified stream.
    /// </summary>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="value">The 32-bit integer to write.</param>
    internal static void WriteInt32(Stream stream, int value)
    {
        value = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Unsafe.As<byte, int>(ref bytes[0]) = value;
        stream.Write(bytes);
    }

    internal static async Task WriteInt16Async(Stream stream, short value, CancellationToken cancellationToken = default)
    {
        byte[] scratchBuffer = ArrayPool<byte>.Shared.Rent(sizeof(short));
        try
        {
            await WriteInt16Async(stream, value, scratchBuffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratchBuffer);
        }
    }

    internal static async ValueTask WriteInt16Async(Stream stream, short value, byte[] scratchBuffer, CancellationToken cancellationToken = default)
    {
        if (scratchBuffer.Length < sizeof(short))
        {
            throw new ArgumentException("Scratch buffer must be at least 2 bytes.", nameof(scratchBuffer));
        }

        value = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        Unsafe.As<byte, short>(ref scratchBuffer[0]) = value;
        await stream.WriteAsync(scratchBuffer.AsMemory(0, sizeof(short)), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteInt32Async(Stream stream, int value, CancellationToken cancellationToken = default)
    {
        byte[] scratchBuffer = ArrayPool<byte>.Shared.Rent(sizeof(int));
        try
        {
            await WriteInt32Async(stream, value, scratchBuffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratchBuffer);
        }
    }

    internal static async ValueTask WriteInt32Async(Stream stream, int value, byte[] scratchBuffer, CancellationToken cancellationToken = default)
    {
        if (scratchBuffer.Length < sizeof(int))
        {
            throw new ArgumentException("Scratch buffer must be at least 4 bytes.", nameof(scratchBuffer));
        }

        value = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
        Unsafe.As<byte, int>(ref scratchBuffer[0]) = value;
        await stream.WriteAsync(scratchBuffer.AsMemory(0, sizeof(int)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Skips a specified number of bytes in the stream.
    /// </summary>
    /// <param name="stream">The stream to skip bytes in.</param>
    /// <param name="num">The number of bytes to skip. Default is 4.</param>
    internal static void Skip4Bytes(Stream stream, int num = 4)
    {
        Span<byte> buffer = stackalloc byte[num];
        //PGUtil.CheckedStreamRead(stream, buffer, 0, 4);
        stream.ReadExactly(buffer);
    }

    internal static async Task Skip4BytesAsync(Stream stream, int num = 4, CancellationToken cancellationToken = default)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(num);
        try
        {
            await Skip4BytesAsync(stream, buffer, num, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async ValueTask Skip4BytesAsync(Stream stream, byte[] scratchBuffer, int num = 4, CancellationToken cancellationToken = default)
    {
        if (num < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(num));
        }
        if (scratchBuffer.Length < num)
        {
            throw new ArgumentException("Scratch buffer is too small for requested skip size.", nameof(scratchBuffer));
        }

        await stream.ReadExactlyAsync(scratchBuffer.AsMemory(0, num), cancellationToken).ConfigureAwait(false);
    }
}

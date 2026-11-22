using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace JustyBase.NetezzaDriver;

/// <summary>
/// Encoded rows for one Netezza external-table import.
/// </summary>
internal sealed class BulkInsertData
{
    internal const int MaxNVarCharCharacters = 16_000;
    internal const int MaxVarcharBytes = 64_000;

    internal const string UsingClause =
        "REMOTESOURCE 'jdbc' DELIMITER '\t' ESCAPECHAR '\\' NULLVALUE '\\N' ENCODING 'UTF8'";

    private const string NullField = "\\N";
    private const int MaxTransferChunkBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly List<byte[]> _rows;

    private BulkInsertData(List<byte[]> rows, int columnCount, string columnDefinitions)
    {
        _rows = rows;
        ColumnCount = columnCount;
        ColumnDefinitions = columnDefinitions;
    }

    internal int RowCount => _rows.Count;
    internal int ColumnCount { get; }
    internal string ColumnDefinitions { get; }

    /// <summary>
    /// Creates an external-table text transfer, or returns null if a value
    /// cannot be represented without changing its meaning.
    /// </summary>
    internal static BulkInsertData? Create(int columnCount, IReadOnlyList<object?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (columnCount < 1 || rows.Count == 0)
            return null;

        var maxCharacters = new int[columnCount];
        var maxBytes = new int[columnCount];
        var renderedRows = new List<string?[]>(rows.Count);

        foreach (var row in rows)
        {
            if (row.Length != columnCount)
                throw new ArgumentException("All source rows must have the same number of columns.", nameof(rows));

            var rendered = new string?[columnCount];
            long rowBytes = Math.Max(0, columnCount - 1) + 1; // delimiters and newline
            for (int column = 0; column < columnCount; column++)
            {
                if (!TryFormatValue(row[column], out var text))
                    return null;

                if (text is null)
                {
                    rowBytes += NullField.Length;
                    if (rowBytes > MaxVarcharBytes)
                        return null;
                    rendered[column] = null;
                    continue;
                }

                if (!CanEscape(text))
                    return null;

                int bytes;
                try
                {
                    bytes = StrictUtf8.GetByteCount(text);
                }
                catch (EncoderFallbackException)
                {
                    return null;
                }
                maxCharacters[column] = Math.Max(maxCharacters[column], text.Length);
                maxBytes[column] = Math.Max(maxBytes[column], bytes);
                rowBytes += EscapedUtf8ByteCount(text);
                if (rowBytes > MaxVarcharBytes)
                    return null;

                rendered[column] = text;
            }

            renderedRows.Add(rendered);
        }

        var definitions = new StringBuilder(columnCount * 24);
        long maximumRowBytes = 0;
        for (int column = 0; column < columnCount; column++)
        {
            int characters = Math.Max(1, maxCharacters[column]);
            int bytes = Math.Max(1, maxBytes[column]);
            string type;
            if (characters <= MaxNVarCharCharacters)
            {
                type = $"nvarchar({characters})";
                maximumRowBytes += bytes;
            }
            else if (bytes <= MaxVarcharBytes)
            {
                type = $"varchar({bytes})";
                maximumRowBytes += bytes;
            }
            else
            {
                return null;
            }

            if (maximumRowBytes > MaxVarcharBytes)
                return null;
            if (column > 0)
                definitions.Append(", ");
            definitions.Append('c').Append(column).Append(' ').Append(type);
        }

        var encodedRows = new List<byte[]>(renderedRows.Count);
        foreach (var row in renderedRows)
        {
            var line = new StringBuilder();
            for (int column = 0; column < row.Length; column++)
            {
                if (column > 0)
                    line.Append('\t');

                if (row[column] is null)
                    line.Append(NullField);
                else
                    AppendEscaped(line, row[column]!);
            }
            line.Append('\n');
            encodedRows.Add(StrictUtf8.GetBytes(line.ToString()));
        }

        return new BulkInsertData(encodedRows, columnCount, definitions.ToString());
    }

    /// <summary>
    /// Streams the encoded rows using the external-table DATA/DONE protocol.
    /// Chunks are capped at 1 MiB even if the server advertises a larger block.
    /// </summary>
    internal void WriteRows(Stream stream, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int chunkSize = EffectiveChunkSize(blockSize);
        byte[] buffer = new byte[chunkSize];
        int used = 0;

        foreach (var row in _rows)
        {
            int offset = 0;
            while (offset < row.Length)
            {
                int count = Math.Min(chunkSize - used, row.Length - offset);
                row.AsSpan(offset, count).CopyTo(buffer.AsSpan(used));
                used += count;
                offset += count;

                if (used == chunkSize)
                {
                    WriteChunk(stream, buffer.AsSpan(0, used));
                    used = 0;
                }
            }
        }

        if (used > 0)
            WriteChunk(stream, buffer.AsSpan(0, used));

        Span<byte> done = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(done, Core.EXTAB_SOCK_DONE);
        stream.Write(done);
        stream.Flush();
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="WriteRows"/>.
    /// </summary>
    internal async Task WriteRowsAsync(
        Stream stream,
        int blockSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        int chunkSize = EffectiveChunkSize(blockSize);
        byte[] buffer = new byte[chunkSize];
        int used = 0;

        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = 0;
            while (offset < row.Length)
            {
                int count = Math.Min(chunkSize - used, row.Length - offset);
                row.AsSpan(offset, count).CopyTo(buffer.AsSpan(used));
                used += count;
                offset += count;

                if (used == chunkSize)
                {
                    await WriteChunkAsync(stream, buffer.AsMemory(0, used), cancellationToken)
                        .ConfigureAwait(false);
                    used = 0;
                }
            }
        }

        if (used > 0)
            await WriteChunkAsync(stream, buffer.AsMemory(0, used), cancellationToken).ConfigureAwait(false);

        byte[] done = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(done, Core.EXTAB_SOCK_DONE);
        await stream.WriteAsync(done, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int EffectiveChunkSize(int blockSize)
        => Math.Clamp(blockSize, 1, MaxTransferChunkBytes);

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> bytes)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, Core.EXTAB_SOCK_DATA);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], bytes.Length);
        stream.Write(header);
        stream.Write(bytes);
        stream.Flush();
    }

    private static async Task WriteChunkAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, Core.EXTAB_SOCK_DATA);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool TryFormatValue(object? value, out string? text)
    {
        if (value is null or DBNull)
        {
            text = null;
            return true;
        }

        switch (value)
        {
            case string s:
                text = s;
                return true;
            case char c:
                text = c.ToString();
                return true;
            case bool b:
                text = b ? "TRUE" : "FALSE";
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return true;
            case float f when float.IsFinite(f):
                text = f.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case double d when double.IsFinite(d):
                text = d.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case DateOnly date:
                text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;
            case TimeOnly time:
                text = time.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                return true;
            case DateTime timestamp:
                text = timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
                return true;
            case TimeSpan timeSpan when timeSpan >= TimeSpan.Zero && timeSpan < TimeSpan.FromDays(1):
                text = timeSpan.ToString(@"hh\:mm\:ss\.ffffff", CultureInfo.InvariantCulture);
                return true;
            case Guid guid:
                text = guid.ToString("D");
                return true;
            default:
                text = null;
                return false;
        }
    }

    private static bool CanEscape(string text)
    {
        foreach (char c in text)
        {
            if (c < 0x20 && c is not ('\t' or '\n' or '\r'))
                return false;
        }
        return true;
    }

    private static int EscapedUtf8ByteCount(string text)
    {
        int bytes = StrictUtf8.GetByteCount(text);
        foreach (char c in text)
        {
            if (c is '\\' or '\t' or '\n' or '\r')
                bytes++;
        }
        return bytes;
    }

    private static void AppendEscaped(StringBuilder output, string text)
    {
        foreach (char c in text)
        {
            if (c is '\\' or '\t' or '\n' or '\r')
                output.Append('\\');
            output.Append(c);
        }
    }
}

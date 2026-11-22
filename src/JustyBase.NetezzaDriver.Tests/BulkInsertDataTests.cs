using JustyBase.NetezzaDriver;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class BulkInsertDataTests
{
    [Fact]
    public void RendersNullsEscapesUnicodeAndSupportedValues()
    {
        var rows = new[]
        {
            new object?[]
            {
                42,
                "a\tb\nc\\d\r",
                null,
                "zażółć 😀",
                new DateOnly(2026, 11, 22),
                new TimeOnly(13, 14, 15),
                "\\N"
            }
        };

        var data = BulkInsertData.Create(rows[0].Length, rows);

        Assert.NotNull(data);
        Assert.Contains("c0 nvarchar(2)", data.ColumnDefinitions, StringComparison.Ordinal);
        Assert.Contains("c2 nvarchar(1)", data.ColumnDefinitions, StringComparison.Ordinal);
        Assert.Contains("c5 nvarchar(15)", data.ColumnDefinitions, StringComparison.Ordinal);

        using var stream = new MemoryStream();
        data.WriteRows(stream, 64);
        Assert.Equal(
            "42\ta\\\tb\\\nc\\\\d\\\r\t\\N\tzażółć 😀\t2026-11-22\t13:14:15.000000\t\\\\N\n",
            ReadDataPayload(stream.ToArray()));
    }

    [Fact]
    public void FormatsNumbersUsingInvariantCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            object?[][] rows = [[12.5m, 1.25d, true]];

            var data = BulkInsertData.Create(3, rows);

            Assert.NotNull(data);
            using var stream = new MemoryStream();
            data.WriteRows(stream, 1024);
            Assert.Equal("12.5\t1.25\tTRUE\n", ReadDataPayload(stream.ToArray()));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void FallsBackForBinaryNonFiniteAndUnsupportedControlValues()
    {
        Assert.Null(BulkInsertData.Create(1, [[new byte[] { 1, 2 }]]));
        Assert.Null(BulkInsertData.Create(1, [[double.NaN]]));
        Assert.Null(BulkInsertData.Create(1, [["contains\0nul"]]));
        Assert.Null(BulkInsertData.Create(1, [["invalid\ud800"]]));
    }

    [Fact]
    public void SelectsVarcharForValuesAboveNVarCharLimitAndRejectsOversizedRows()
    {
        var largeText = new string('x', BulkInsertData.MaxNVarCharCharacters + 1);
        var data = BulkInsertData.Create(1, [[largeText]]);

        Assert.NotNull(data);
        Assert.Equal($"c0 varchar({largeText.Length})", data.ColumnDefinitions);
        Assert.Null(BulkInsertData.Create(1, [[new string('x', BulkInsertData.MaxVarcharBytes + 1)]]));
    }

    [Fact]
    public void StreamsMultipleDataFramesAndTerminatesWithDone()
    {
        var data = BulkInsertData.Create(1, [[new string('a', 13)], [new string('b', 9)]])!;
        using var stream = new MemoryStream();

        data.WriteRows(stream, 5);

        var frames = ReadFrames(stream.ToArray());
        Assert.Equal(5, frames.DataLengths.Count);
        Assert.All(frames.DataLengths, length => Assert.InRange(length, 1, 5));
        Assert.Equal("aaaaaaaaaaaaa\nbbbbbbbbb\n", Encoding.UTF8.GetString(frames.Payload));
        Assert.Equal(Core.EXTAB_SOCK_DONE, frames.Done);
    }

    [Fact]
    public async Task AsyncWriterUsesTheSameExternalTableFrames()
    {
        var data = BulkInsertData.Create(2, [[1, "async"], [2, "zażółć"]])!;
        using var stream = new MemoryStream();

        await data.WriteRowsAsync(stream, 8, TestContext.Current.CancellationToken);

        var frames = ReadFrames(stream.ToArray());
        Assert.Equal("1\tasync\n2\tzażółć\n", Encoding.UTF8.GetString(frames.Payload));
        Assert.Equal(Core.EXTAB_SOCK_DONE, frames.Done);
    }

    [Fact]
    public void RejectsRowsWithDifferentColumnCounts()
    {
        object?[][] rows = [[1, 2], [3]];

        Assert.Throws<ArgumentException>(() => BulkInsertData.Create(2, rows));
    }

    [Fact]
    public void BulkInsertConnectionStringOptionDefaultsOnAndCanBeDisabled()
    {
        var defaultConnection = new NzConnection("User=test;Password=p;Host=localhost;Database=db");
        Assert.True(defaultConnection.BulkInsertEnabled);

        var builder = new NzConnectionStringBuilder
        {
            UserName = "test",
            Password = "p",
            Host = "localhost",
            Database = "db",
            BulkInsert = false
        };
        var disabledConnection = new NzConnection(builder.ConnectionString);

        Assert.False(disabledConnection.BulkInsertEnabled);
        Assert.Contains("BulkInsert=False", disabledConnection.ConnectionString, StringComparison.Ordinal);
        Assert.Contains("BulkInsert=False", disabledConnection.SafeConnectionString, StringComparison.Ordinal);
    }

    private static string ReadDataPayload(byte[] bytes)
        => Encoding.UTF8.GetString(ReadFrames(bytes).Payload);

    private static (byte[] Payload, List<int> DataLengths, int Done) ReadFrames(byte[] bytes)
    {
        var payload = new MemoryStream();
        var lengths = new List<int>();
        int offset = 0;
        while (offset + 4 <= bytes.Length)
        {
            int type = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (type == Core.EXTAB_SOCK_DONE)
                return (payload.ToArray(), lengths, type);

            Assert.Equal(Core.EXTAB_SOCK_DATA, type);
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            Assert.InRange(length, 0, bytes.Length - offset);
            lengths.Add(length);
            payload.Write(bytes, offset, length);
            offset += length;
        }

        throw new Xunit.Sdk.XunitException("External-table stream did not contain a DONE frame.");
    }
}

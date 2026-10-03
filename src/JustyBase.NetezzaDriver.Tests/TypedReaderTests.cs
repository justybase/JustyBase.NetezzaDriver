using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class TypedReaderTests
{
    private static (NzConnection Conn, NzCommand Cmd, NzDataReader Reader) CreateReaderWithRow(RowValue[] row)
    {
        var conn = new NzConnection("Host=localhost;Database=test;User=u;Password=p");
        var cmd = new NzCommand(conn);
        cmd.AddRow(row);
        var reader = NzDataReader.CreateForTests(cmd);
        return (conn, cmd, reader);
    }

    [Fact]
    public void GetFieldValue_Int_ReturnsWithoutBoxing_SameAsGetInt32()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.Int32;
        row[0].int32Value = 12345;
        var (_, _, reader) = CreateReaderWithRow(row);
        Assert.Equal(12345, reader.GetInt32(0));
        Assert.Equal(12345, reader.GetFieldValue<int>(0));
        Assert.Equal((object)12345, reader.GetValue(0));
    }

    [Fact]
    public void GetFieldValue_Long_ReturnsWithoutBoxing()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.Int64;
        row[0].int64Value = 9876543210L;
        var (_, _, reader) = CreateReaderWithRow(row);
        Assert.Equal(9876543210L, reader.GetInt64(0));
        Assert.Equal(9876543210L, reader.GetFieldValue<long>(0));
    }

    [Fact]
    public void GetFieldValue_String_ReturnsReference()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.String;
        row[0].stringValue = "hello";
        var (_, _, reader) = CreateReaderWithRow(row);
        Assert.Equal("hello", reader.GetString(0));
        Assert.Equal("hello", reader.GetFieldValue<string>(0));
    }

    [Fact]
    public void GetFieldValue_Mismatch_ThrowsSameAsBefore()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.Int32;
        row[0].int32Value = 1;
        var (_, _, reader) = CreateReaderWithRow(row);
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<long>(0));
    }

    [Fact]
    public void GetFieldValue_DbNull_PreservesSemantics()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.DBNull;
        var (_, _, reader) = CreateReaderWithRow(row);
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<int>(0));
    }

    [Fact]
    public void GetBytes_FullRead_ZeroTemp_ReturnsByteCount()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.String;
        row[0].stringValue = "héllo world";
        var (_, _, reader) = CreateReaderWithRow(row);
        int expected = System.Text.Encoding.UTF8.GetByteCount("héllo world");
        Assert.Equal(expected, reader.GetBytes(0, 0, null, 0, 0));

        byte[] buf = new byte[expected];
        long read = reader.GetBytes(0, 0, buf, 0, buf.Length);
        Assert.Equal(expected, read);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("héllo world"), buf);
    }

    [Fact]
    public void GetBytes_Chunked_PreservesByteExactSemantics()
    {
        string text = new string('a', 100) + "é" + new string('b', 100);
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.String;
        row[0].stringValue = text;
        var (_, _, reader) = CreateReaderWithRow(row);
        byte[] full = System.Text.Encoding.UTF8.GetBytes(text);

        byte[] chunked = new byte[full.Length];
        int offset = 0;
        while (offset < full.Length)
        {
            int toRead = Math.Min(7, full.Length - offset);
            byte[] buf = new byte[7];
            long got = reader.GetBytes(0, offset, buf, 0, toRead);
            Assert.True(got > 0);
            Array.Copy(buf, 0, chunked, offset, got);
            offset += (int)got;
        }
        Assert.Equal(full, chunked);
    }

    [Fact]
    public void GetChars_String_NoBoxing_Partial()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.String;
        row[0].stringValue = "abcdefghij";
        var (_, _, reader) = CreateReaderWithRow(row);
        Assert.Equal(10, reader.GetChars(0, 0, null, 0, 0));
        char[] buf = new char[4];
        Assert.Equal(4, reader.GetChars(0, 2, buf, 0, 4));
        Assert.Equal("cdef", new string(buf));
    }

    [Fact]
    public void GetChars_Int_DoesNotBox()
    {
        var row = new RowValue[1];
        row[0].typeCode = TypeCodeEx.Int32;
        row[0].int32Value = 12345;
        var (_, _, reader) = CreateReaderWithRow(row);
        Assert.Equal(5, reader.GetChars(0, 0, null, 0, 0));
        char[] buf = new char[5];
        reader.GetChars(0, 0, buf, 0, 5);
        Assert.Equal("12345", new string(buf));
    }
}

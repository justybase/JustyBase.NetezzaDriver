using JustyBase.NetezzaDriver.StringPool;
using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class NzConnectionHelpers
{
    internal static readonly Encoding ClientEncoding = Encoding.UTF8;
    internal static readonly Encoding CharVarcharEncoding = Encoding.Latin1;
    internal const string NZPY_CLIENT_VERSION = "Release 11.1.0.0";

    //add pooling ? 
    internal static string TextRecv(byte[] data, int offset, int length, Sylvan? sp)
    {
        if (length + offset > data.Length)
        {
            length = data.Length - offset;
        Debug.Assert(false);
        }

        if (sp is not null)
        {
            return sp.GetString(data.AsSpan(offset, length), ClientEncoding);
        }
        else
        {
            return ClientEncoding.GetString(data, offset, length);
        }
    }

    /// <summary>
    /// SELECT * FROM SYSTEM.ADMIN._T_PROC ORDER BY ROWID LIMIT 1000
    /// </summary>
    /// <param name="data"></param>
    /// <param name="offset"></param>
    /// <param name="length"></param>
    /// <returns></returns>
    internal static string ByteaRecv(byte[] data, int offset, int length)
    {
        //byte[] result = new byte[length];
        //Array.Copy(data, offset, result, 0, length);
        //return result;
        return Encoding.ASCII.GetString(data, offset, length);
    }

    internal static bool BoolRecvTyped(byte[] data, int offset, int length)
    {
        return data[offset] == 116; // ASCII for 't'= (byte)'t', (byte)'f' = 102
    }

    //change to sbyte ?, OID 2500
    internal static Int16 ByteRecvTyped(byte[] data, int offset, int length)
    {
        //Int16 to be in pair with ODBC
        return (Int16)SByte.Parse(data.AsSpan().Slice(offset, length));
    }

    internal static long Int8RecvTyped(byte[] data, int offset, int length)
    {
        return long.Parse(data.AsSpan().Slice(offset, length));
    }

    internal static Int16 Int2RecvTyped(byte[] data, int offset, int length)
    {
        return Int16.Parse(data.AsSpan().Slice(offset, length));
    }

    internal static int Int4RecvTyped(byte[] data, int offset, int length)
    {
        return Int32.Parse(data.AsSpan().Slice(offset, length));
    }

    internal static float Float4RecvTyped(byte[] data, int offset, int length)
    {
        return float.Parse(data.AsSpan().Slice(offset, length), CultureInfo.InvariantCulture);
    }

    internal static double Float8RecvTyped(byte[] data, int offset, int length)
    {
        return double.Parse(data.AsSpan().Slice(offset, length), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts PostgreSQL numeric format to C# decimal
    /// occurs for SELECT 3.14::NUMERIC(10,4)
    /// </summary>
    internal static decimal NumericInTyped(byte[] data, int offset, int length)
    {
        var sp = new ReadOnlySpan<byte>(data, offset, length);
        return decimal.Parse(sp, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Receives timestamp with timezone as integer from PostgreSQL data stream
    /// </summary>
    //private object TimestamptzRecvInteger(byte[] data, int offset, int length)
    //{
    //    Debug.Fail("TimestamptzRecvInteger usage");
    //    //
    //    //return Encoding.UTF8.GetString(data, offset, length);
    //    return Encoding.ASCII.GetString(data, offset, length);
    //}

    /// <summary>
    /// Converts C# decimal to PostgreSQL numeric format
    /// </summary>
    //private byte[] NumericOut(decimal d)
    //{
    //    Debug.Assert(false);
    //    //Encoding.ASCII ?
    //    return _clientEncoding.GetBytes(d.ToString(CultureInfo.InvariantCulture));
    //}

    /// <summary>
    /// Receives interval as integer from PostgreSQL data stream
    /// SELECT '5 hours 41 minutes  15 sec'::interval
    /// </summary>
    internal static string IntervalRecvInteger(byte[] data, int offset, int length)
    {
        return Encoding.ASCII.GetString(data, offset, length);
    }

    /// <summary>
    /// Receives UUID from data stream
    /// TODO 
    /// </summary>
    internal static Guid UuidRecvTyped(byte[] data, int offset, int length)
    {
        Debug.Assert(false);
        if (length != 16)
        {
            throw new ArgumentException("UUID must be exactly 16 bytes");
        }
        return new Guid(data.AsSpan().Slice(offset, length));
    }

}

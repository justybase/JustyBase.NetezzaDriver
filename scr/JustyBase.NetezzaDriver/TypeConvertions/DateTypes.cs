using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace JustyBase.NetezzaDriver.TypeConvertions;

internal static class DateTypes
{
    private const string format1 = "hh";
    private const string format2 = "hh\\:mm";
    private const string format3 = "hh\\:mm\\:ss";

    private static readonly CultureInfo _invariantCulture = CultureInfo.InvariantCulture;

    //[SkipLocalsInit]
    internal static string TimetzOutTimetzadt(TimeSpan timeSpanVal, int timetzZone)
    {
        var timeSpanTimeZone = TimeSpan.FromSeconds(-timetzZone);
        char tzSign = timetzZone < 0 ? '+' : '-';

        Span<char> chars = stackalloc char[32];
        timeSpanVal.TryFormat(chars, out int written,"g", _invariantCulture);
        chars[written] = tzSign;
        written++;

        string format = format1;
        if (timeSpanTimeZone.Seconds != 0)
        {
            format = format3;
        }
        else if (timeSpanTimeZone.Minutes != 0)
        {
            format = format2;
        }
        timeSpanTimeZone.TryFormat(chars[written..], out int written2, format, _invariantCulture);
        return chars.Slice(0, written + written2).ToString();
    }


    /// <summary>
    /// Converts a timestamp from Netezza to a DateTime, where the timestamp is a 32-bit integer representing seconds since Unix epoch.
    /// </summary>
    /// <param name="data">The data containing the timestamp.</param>
    /// <returns>The converted DateTime value.</returns>
    internal static DateTime TimestampRecvInt(Span<byte> data)
    {
        int seconds = BitConverter.ToInt32(data);
        return DateTime.UnixEpoch.AddSeconds(seconds);
    }

    /// <summary>
    /// Convert a timestamp from Netezza to a TimeSpan, where the timestamp is a 64-bit integer representing microseconds.
    /// used for standard (non system) tables.
    /// C# timestamp do not support months, so we need to convert it to a string.
    /// todo years
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static /*DateTime*/string TimeRecvFloatX1(Span<byte> data)
    {
        long micros = BitConverter.ToInt64(data);
        var ts = new TimeSpan(micros * TimeSpan.TicksPerMicrosecond);
        int months = BitConverter.ToInt32(data[8..]);
        if (months == 0)
        {
            return string.Format(_invariantCulture, "{0}", ts);
        }
        else
        {
            int years = months / 12;
            months %= 12;
            if (years > 0)
            {
                return string.Format(_invariantCulture, "{0} years {1} mons {2}", years, months, ts);
            }

            return string.Format(_invariantCulture, "{0} mons {1}", months, ts);
        }
    }

    /// <summary>
    /// Convert a timestamp from Netezza to a TimeSpan, where the timestamp is a 64-bit integer representing microseconds.
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static TimeSpan TimeRecvFloatX2(Span<byte> data)
    {
        long micros = BitConverter.ToInt64(data);
        return TimeSpan.FromMicroseconds(micros);
    }

    private const long PostgresTimestampOffsetTicks = 630822816000000000L;//2000-01-01

    internal static DateTime ToDateTimeFrom8Bytes(ReadOnlySpan<byte> spanData)
    {
        return new DateTime(BitConverter.ToInt64(spanData) * 10 + PostgresTimestampOffsetTicks);
    }

    /// <summary>
    /// add days to 2000-01-01
    /// </summary>
    /// <param name="spanData"></param>
    /// <returns></returns>
    internal static DateTime ToDateTimeFrom4Bytes(ReadOnlySpan<byte> spanData)
    {
        return new DateTime(PostgresTimestampOffsetTicks).AddDays(BitConverter.ToInt32(spanData));
    }


    /// <summary>
    /// occurs for SELECT '2024-12-12'::DATE
    /// </summary>
    /// <param name="data"></param>
    /// <param name="offset"></param>
    /// <param name="length"></param>
    /// <returns></returns>
    internal static DateTime DateInTyped(byte[] data, int offset, int length)
    {
        //string dateStr = _clientEncoding.GetString(data, offset, length);
        ReadOnlySpan<byte> spB = new ReadOnlySpan<byte>(data, offset, length);
        try
        {
            return new DateTime(
                int.Parse(spB[0..4]),  // year
                int.Parse(spB[5..7]),  // month
                int.Parse(spB[8..10])); // day
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// to do eliminate allocation
    /// </summary>
    /// <param name="data"></param>
    /// <param name="offset"></param>
    /// <param name="length"></param>
    /// <returns></returns>
    internal static DateTime TimestamptzRecvFloatTyped(byte[] data, int offset, int length)
    {
        Span<char> chars = length < 128 ? stackalloc char[length] : new char[length];
        int charCnt = Encoding.ASCII.GetChars(data.AsSpan().Slice(offset, length), chars);
        chars = chars[..charCnt];

        if (DateTime.TryParse(chars, out var res))
        {
            return res;
        }
        else
        {
            //return new string(chars);
            return DateTime.MinValue;
        }
    }

    internal static TimeSpan TimeInTyped(byte[] data, int offset, int length)
    {
        int hour = int.Parse(data.AsSpan().Slice(offset, 2));
        int minute = int.Parse(data.AsSpan().Slice(offset + 3, 2));
        decimal seconds = decimal.Parse(data.AsSpan().Slice(offset + 6, length - 6), _invariantCulture);

        return new TimeSpan(
            0,
            hour,
            minute,
            (int)seconds,
            (int)((seconds - Math.Floor(seconds)) * 1000000));
    }

    private const double EPOCH_SECONDS = 946684800.0; // 2000-01-01 UTC

    internal static DateTime TimestampRecvFloatTyped(byte[] data, int offset, int length)
    {
        double seconds = BitConverter.ToDouble(data, offset);
        return DateTime.UnixEpoch.AddSeconds(EPOCH_SECONDS + seconds);
    }
}


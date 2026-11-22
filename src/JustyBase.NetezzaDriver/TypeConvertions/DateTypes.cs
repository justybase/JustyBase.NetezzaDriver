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
        string timeText = FormatClock(timeSpanVal);
        timeText.AsSpan().CopyTo(chars);
        int written = timeText.Length;
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
    internal static DateTime TimestampRecvInt(ReadOnlySpan<byte> data)
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
    internal static /*DateTime*/string TimeRecvFloatX1(ReadOnlySpan<byte> data)
    {
        long micros = BitConverter.ToInt64(data);
        int months = BitConverter.ToInt32(data[8..]);
        return FormatInterval(micros, months);
    }

    /// <summary>
    /// Formats a Netezza interval exactly as the appliance prints it: separate
    /// signed year/month and day/time components, six fractional digits with
    /// trailing zeros removed. A plain <see cref="TimeSpan"/> string is not
    /// sufficient because it collapses days into hours and loses the month
    /// component entirely (the old code also dropped negative years).
    /// </summary>
    /// <summary>
    /// Formats a time-of-day value with two-digit hours and up to seven
    /// fractional digits (trailing zeros omitted). TimeSpan's general "g"
    /// format emits a single-digit hour, which does not match the appliance.
    /// </summary>
    internal static string FormatClock(TimeSpan value)
    {
        string text = $"{value.Hours:D2}:{value.Minutes:D2}:{value.Seconds:D2}";
        long fractionTicks = value.Ticks % TimeSpan.TicksPerSecond;
        if (fractionTicks != 0)
        {
            text += "." + (fractionTicks / 10).ToString("D6", _invariantCulture).TrimEnd('0');
        }
        return text;
    }

    internal static string FormatInterval(long micros, int months)
    {
        var parts = new List<string>(3);

        int years = months / 12;
        int remainingMonths = months % 12;
        if (years != 0)
        {
            parts.Add($"{years} {(Math.Abs(years) == 1 ? "year" : "years")}");
        }
        if (remainingMonths != 0)
        {
            parts.Add($"{remainingMonths} mons");
        }

        if (micros != 0)
        {
            bool negative = micros < 0;
            long absolute = negative ? -micros : micros;
            long days = absolute / 86_400_000_000L;
            long remainder = absolute % 86_400_000_000L;
            int hours = (int)(remainder / 3_600_000_000L);
            remainder %= 3_600_000_000L;
            int minutes = (int)(remainder / 60_000_000L);
            remainder %= 60_000_000L;
            int seconds = (int)(remainder / 1_000_000L);
            int fractional = (int)(remainder % 1_000_000L);

            string sign = negative ? "-" : string.Empty;
            if (days != 0)
            {
                parts.Add($"{sign}{days} {(days == 1 ? "day" : "days")}");
            }

            string time = $"{sign}{hours:D2}:{minutes:D2}:{seconds:D2}";
            if (fractional != 0)
            {
                time += "." + fractional.ToString("D6", _invariantCulture).TrimEnd('0');
            }
            parts.Add(time);
        }

        return parts.Count == 0 ? "00:00:00" : string.Join(" ", parts);
    }

    /// <summary>
    /// Convert a timestamp from Netezza to a TimeSpan, where the timestamp is a 64-bit integer representing microseconds.
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    internal static TimeSpan TimeRecvFloatX2(ReadOnlySpan<byte> data)
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
        ReadOnlySpan<byte> spB = new ReadOnlySpan<byte>(data, offset, length);
        return new DateTime(
            int.Parse(spB[0..4]),  // year
            int.Parse(spB[5..7]),  // month
            int.Parse(spB[8..10])); // day
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

        throw new FormatException($"Invalid timestamp with time zone value '{chars.ToString()}'.");
    }

    internal static TimeSpan TimeInTyped(byte[] data, int offset, int length)
    {
        int hour = int.Parse(data.AsSpan().Slice(offset, 2));
        int minute = int.Parse(data.AsSpan().Slice(offset + 3, 2));
        decimal seconds = decimal.Parse(data.AsSpan().Slice(offset + 6, length - 6), _invariantCulture);

        // TimeSpan's (days, hours, minutes, seconds, milliseconds) constructor
        // takes milliseconds, not microseconds. The previous code passed the
        // fractional microseconds and produced a value 1000x too large.
        long totalMicroseconds = (long)decimal.Round(seconds * 1_000_000m, MidpointRounding.AwayFromZero);
        return new TimeSpan(0, hour, minute, 0) + TimeSpan.FromMicroseconds(totalMicroseconds);
    }

    private const double EPOCH_SECONDS = 946684800.0; // 2000-01-01 UTC

    internal static DateTime TimestampRecvFloatTyped(byte[] data, int offset, int length)
    {
        double seconds = BitConverter.ToDouble(data, offset);
        return DateTime.UnixEpoch.AddSeconds(EPOCH_SECONDS + seconds);
    }
}


using JustyBase.NetezzaDriver.TypeConvertions;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class DateTypesTests
{
    private const long Day = 86_400_000_000L;

    [Fact]
    public void FormatInterval_Zero_ReturnsClockZero()
    {
        Assert.Equal("00:00:00", DateTypes.FormatInterval(0, 0));
    }

    [Fact]
    public void FormatInterval_Positive_KeepsYearsMonthsDaysAndFraction()
    {
        long micros = 3 * Day + 4 * 3_600_000_000L + 5 * 60_000_000L + 6 * 1_000_000L + 789_012L;
        Assert.Equal(
            "1 year 2 mons 3 days 04:05:06.789012",
            DateTypes.FormatInterval(micros, 14));
    }

    [Fact]
    public void FormatInterval_Negative_PreservesEverySign()
    {
        long micros = -(3 * Day + 4 * 3_600_000_000L + 5 * 60_000_000L + 6 * 1_000_000L + 789_012L);
        Assert.Equal(
            "-1 year -2 mons -3 days -04:05:06.789012",
            DateTypes.FormatInterval(micros, -14));
    }

    [Theory]
    [InlineData(12, 0, "1 year")]
    [InlineData(-12, 0, "-1 year")]
    [InlineData(24, 0, "2 years")]
    [InlineData(2, 0, "2 mons")]
    [InlineData(-2, 0, "-2 mons")]
    public void FormatInterval_MonthsOnly_PluralizesYears(int months, long micros, string expected)
    {
        Assert.Equal(expected, DateTypes.FormatInterval(micros, months));
    }

    [Fact]
    public void FormatInterval_SingleDay_UsesSingular()
    {
        Assert.Equal("1 day 00:00:01", DateTypes.FormatInterval(Day + 1_000_000L, 0));
    }

    [Fact]
    public void FormatInterval_ReadsRawWireLayout()
    {
        byte[] data = new byte[12];
        BitConverter.TryWriteBytes(
            data.AsSpan(0, 8),
            -(3 * Day + 4 * 3_600_000_000L + 5 * 60_000_000L + 6 * 1_000_000L + 789_012L));
        BitConverter.TryWriteBytes(data.AsSpan(8, 4), -14);

        Assert.Equal("-1 year -2 mons -3 days -04:05:06.789012", DateTypes.TimeRecvFloatX1(data));
    }

    [Fact]
    public void FormatClock_PadsHoursAndTrimsTrailingFractionalZeros()
    {
        Assert.Equal("00:00:00", DateTypes.FormatClock(TimeSpan.Zero));
        Assert.Equal("01:02:03", DateTypes.FormatClock(new TimeSpan(1, 2, 3)));
        Assert.Equal("12:34:56.123456", DateTypes.FormatClock(new TimeSpan(0, 12, 34, 56, 123).Add(TimeSpan.FromTicks(4560))));
        Assert.Equal("23:59:59.999999", DateTypes.FormatClock(new TimeSpan(0, 23, 59, 59, 999).Add(TimeSpan.FromTicks(9999))));
    }

    [Fact]
    public void TimetzOutTimetzadt_FormatsOffsetWithTwoDigitHours()
    {
        Assert.Equal("12:34:56+05:30", DateTypes.TimetzOutTimetzadt(new TimeSpan(12, 34, 56), -19_800));
        Assert.Equal("00:00:00-00", DateTypes.TimetzOutTimetzadt(TimeSpan.Zero, 0));
        Assert.Equal("23:59:59.999999-01", DateTypes.TimetzOutTimetzadt(new TimeSpan(0, 23, 59, 59, 999).Add(TimeSpan.FromTicks(9999)), 3_600));
    }

    [Fact]
    public void EpochConverters_UseTheirDocumentedBases()
    {
        byte[] seconds = BitConverter.GetBytes(0);
        Assert.Equal(DateTime.UnixEpoch, DateTypes.TimestampRecvInt(seconds));

        byte[] microseconds = BitConverter.GetBytes(1_500_000L);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), DateTypes.TimeRecvFloatX2(microseconds));

        byte[] days = BitConverter.GetBytes(1);
        Assert.Equal(new DateTime(2000, 1, 2), DateTypes.ToDateTimeFrom4Bytes(days));

        byte[] pgMicroseconds = BitConverter.GetBytes(1_000_000L);
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 1), DateTypes.ToDateTimeFrom8Bytes(pgMicroseconds));
    }

    [Fact]
    public void TypedTextParsers_RejectMalformedValues()
    {
        Assert.Equal(new DateTime(2024, 2, 29), DateTypes.DateInTyped("2024-02-29"u8.ToArray(), 0, 10));
        Assert.Equal(new TimeSpan(12, 34, 56), DateTypes.TimeInTyped("12:34:56"u8.ToArray(), 0, 8));

        Assert.Throws<FormatException>(() =>
            DateTypes.TimestamptzRecvFloatTyped("not-a-timestamp"u8.ToArray(), 0, 15));
    }
}

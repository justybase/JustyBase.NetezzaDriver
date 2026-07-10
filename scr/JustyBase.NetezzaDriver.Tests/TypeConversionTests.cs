using JustyBase.NetezzaDriver.TypeConvertions;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public class TypeConversionTests
{
    [Fact]
    public void DateInTyped_ThrowsForInvalidDate()
    {
        byte[] data = "2024-99-99"u8.ToArray();

        Assert.ThrowsAny<Exception>(() => DateTypes.DateInTyped(data, 0, data.Length));
    }

    [Fact]
    public void TimestamptzRecvFloatTyped_ThrowsForInvalidTimestamp()
    {
        byte[] data = "not-a-timestamp"u8.ToArray();

        var ex = Assert.Throws<FormatException>(() => DateTypes.TimestamptzRecvFloatTyped(data, 0, data.Length));

        Assert.Contains("Invalid timestamp", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetCsNumeric_ThrowsWhenNumericPayloadIsInvalid()
    {
        byte[] data = [];

        Assert.ThrowsAny<Exception>(() => Numeric.GetCsNumeric(data, prec: 10, scale: 2, digitCount: 4));
    }
}

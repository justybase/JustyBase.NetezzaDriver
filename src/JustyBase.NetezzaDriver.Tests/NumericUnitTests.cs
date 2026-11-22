using JustyBase.NetezzaDriver.TypeConvertions;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class NumericUnitTests
{
    [Fact]
    public void GetNumericString_Zero_PreservesScale()
    {
        Assert.Equal(
            "0.0000000000000000000",
            Numeric.GetNumericString(new byte[16], prec: 38, scale: 19, digitCount: 4));
    }

    [Fact]
    public void GetNumericString_Positive_UsesMostSignificantLimbFirst()
    {
        byte[] data = new byte[16];
        BitConverter.TryWriteBytes(data.AsSpan(12, 4), 31_400u);

        Assert.Equal("3.1400", Numeric.GetNumericString(data, prec: 38, scale: 4, digitCount: 4));
    }

    [Fact]
    public void GetNumericString_Negative_UsesTwosComplement()
    {
        byte[] data = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        Assert.Equal(
            "-0.0000000000000000001",
            Numeric.GetNumericString(data, prec: 38, scale: 19, digitCount: 4));
    }

    [Fact]
    public void GetNumericString_Maximum38Digits_IsExact()
    {
        byte[] data = Convert.FromHexString("A84C3B4B7AC4865A3F228A09FFFFFFFF");
        Assert.Equal(
            "9999999999999999999.9999999999999999999",
            Numeric.GetNumericString(data, prec: 38, scale: 19, digitCount: 4));
    }

    [Fact]
    public void GetCsNumeric_FastPath_PreservesScale()
    {
        byte[] data = BitConverter.GetBytes(31_400u);
        Assert.Equal(3.1400m, Numeric.GetCsNumeric(data, prec: 9, scale: 4, digitCount: 1));
    }

    [Fact]
    public void GetCsNumeric_TwoLimbs_CombinesHighAndLowParts()
    {
        byte[] lowOnly = new byte[8];
        BitConverter.TryWriteBytes(lowOnly.AsSpan(4, 4), 1u);
        Assert.Equal(1m, Numeric.GetCsNumeric(lowOnly, prec: 18, scale: 0, digitCount: 2));

        byte[] highOnly = new byte[8];
        BitConverter.TryWriteBytes(highOnly.AsSpan(0, 4), 1u);
        Assert.Equal(4_294_967_296m, Numeric.GetCsNumeric(highOnly, prec: 18, scale: 0, digitCount: 2));
    }

    [Fact]
    public void VarbinaryInTyped_AcceptsBothHexSpellingsAndFallsBackToRawText()
    {
        byte[] hex = "X'0a0B'"u8.ToArray();
        Assert.Equal([0x0A, 0x0B], NzConnectionHelpers.VarbinaryInTyped(hex, 0, hex.Length));

        byte[] backslash = "\\x0a0b"u8.ToArray();
        Assert.Equal([0x0A, 0x0B], NzConnectionHelpers.VarbinaryInTyped(backslash, 0, backslash.Length));

        byte[] plain = "  hello  "u8.ToArray();
        Assert.Equal("  hello  "u8.ToArray(), NzConnectionHelpers.VarbinaryInTyped(plain, 0, plain.Length));

        byte[] odd = "X'0'"u8.ToArray();
        Assert.Equal(odd, NzConnectionHelpers.VarbinaryInTyped(odd, 0, odd.Length));

        byte[] invalid = "X'GG'"u8.ToArray();
        Assert.Equal(invalid, NzConnectionHelpers.VarbinaryInTyped(invalid, 0, invalid.Length));
    }

    [Fact]
    public void VarbinaryInTyped_RejectsOutOfRangeSlices()
    {
        byte[] data = [1, 2, 3];
        Assert.Throws<ArgumentOutOfRangeException>(() => NzConnectionHelpers.VarbinaryInTyped(data, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => NzConnectionHelpers.VarbinaryInTyped(data, -1, 1));
    }

    [Fact]
    public void TextAndScalarReaders_DecodeExpectedValues()
    {
        byte[] text = "Zażółć"u8.ToArray();
        Assert.Equal("Zażółć", NzConnectionHelpers.TextRecv(text, 0, text.Length, null));

        Assert.True(NzConnectionHelpers.BoolRecvTyped("t"u8.ToArray(), 0, 1));
        Assert.False(NzConnectionHelpers.BoolRecvTyped("f"u8.ToArray(), 0, 1));
        Assert.Equal((short)-5, NzConnectionHelpers.ByteRecvTyped("-5"u8.ToArray(), 0, 2));
        Assert.Equal((short)-1234, NzConnectionHelpers.Int2RecvTyped("-1234"u8.ToArray(), 0, 5));
        Assert.Equal(-123456, NzConnectionHelpers.Int4RecvTyped("-123456"u8.ToArray(), 0, 7));
        Assert.Equal(-1234567890123, NzConnectionHelpers.Int8RecvTyped("-1234567890123"u8.ToArray(), 0, 14));
        Assert.Equal(1.5f, NzConnectionHelpers.Float4RecvTyped("1.5"u8.ToArray(), 0, 3));
        Assert.Equal(-2.25, NzConnectionHelpers.Float8RecvTyped("-2.25"u8.ToArray(), 0, 5));
        Assert.Equal(3.1400m, NzConnectionHelpers.NumericInTyped("3.1400"u8.ToArray(), 0, 6));
        Assert.Equal("1 day", NzConnectionHelpers.IntervalRecvInteger("1 day"u8.ToArray(), 0, 5));
    }

    [Fact]
    public void UuidRecvTyped_RequiresSixteenBytes()
    {
        byte[] bytes = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var expected = new Guid(bytes);
        Assert.Equal(expected, NzConnectionHelpers.UuidRecvTyped(bytes, 0, 16));
        Assert.Throws<ArgumentException>(() => NzConnectionHelpers.UuidRecvTyped(bytes, 0, 15));
    }

    [Fact]
    public void TextRecv_ClampsLengthToTheAvailableBuffer()
    {
        byte[] text = "ab"u8.ToArray();
        Assert.Equal("ab", NzConnectionHelpers.TextRecv(text, 0, 99, null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(18)]
    [InlineData(28)]
    public void NumericPrecisionWithinDecimalRange_AdvertisesDecimal(int precision)
    {
        Assert.Equal(typeof(decimal), NumericField(precision).Type);
        Assert.False(Numeric.ExceedsDecimalPrecision(precision));
    }

    [Theory]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(38)]
    public void NumericPrecisionAboveDecimalRange_AdvertisesString(int precision)
    {
        Assert.Equal(typeof(string), NumericField(precision).Type);
        Assert.True(Numeric.ExceedsDecimalPrecision(precision));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void NumericWithoutDeclaredPrecision_AdvertisesDecimal(int typeModifier)
    {
        var field = new FieldDescription { TypeOID = 1700, TypeModifier = typeModifier };
        Assert.Equal(typeof(decimal), field.Type);
    }

    private static FieldDescription NumericField(int precision)
        => new()
        {
            TypeOID = 1700,
            TypeModifier = 16 + (precision << 16),
        };
}

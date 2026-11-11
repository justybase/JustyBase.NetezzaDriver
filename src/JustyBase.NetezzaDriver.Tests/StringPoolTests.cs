using System.Text;
using JustyBase.NetezzaDriver;
using JustyBase.NetezzaDriver.StringPool;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class StringPoolTests
{
    [Fact]
    public void ByteLastHitReturnsSameInstanceForRepeatedValue()
    {
        var pool = new Sylvan();
        byte[] bytes = Encoding.UTF8.GetBytes("PAID");

        string first = pool.GetString(bytes, Encoding.UTF8);
        string second = pool.GetString(bytes, Encoding.UTF8);

        Assert.Equal("PAID", first);
        Assert.Same(first, second);
    }

    [Fact]
    public void ByteLastHitIsNotReusedAcrossDifferentEncodings()
    {
        var pool = new Sylvan();
        // 0xC3 0xA9 is "é" in UTF-8 and "Ã©" in Latin1.
        byte[] bytes = [0xC3, 0xA9];

        string latin1 = pool.GetString(bytes, Encoding.Latin1);
        string utf8 = pool.GetString(bytes, Encoding.UTF8);

        Assert.Equal("Ã©", latin1);
        Assert.Equal("é", utf8);
    }

    [Fact]
    public void InternsRepeatedValuesBelowTheCap()
    {
        var pool = new Sylvan();

        string first = pool.GetString("dup".AsSpan());
        string second = pool.GetString("dup".AsSpan());

        Assert.Equal("dup", first);
        Assert.Same(first, second);
    }

    [Fact]
    public void StopsInterningAfterCapButStillReturnsCorrectValues()
    {
        var pool = new Sylvan();
        const int aboveCap = 4100; // Sylvan.MaxEntries is 4096

        for (int i = 0; i < aboveCap; i++)
        {
            Assert.Equal($"v{i}", pool.GetString($"v{i}".AsSpan()));
        }

        // Above the cap, values are allocated but remain correct.
        string a = pool.GetString("v4099".AsSpan());
        string b = pool.GetString("v4099".AsSpan());
        Assert.Equal("v4099", a);
        Assert.Equal(a, b);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void LongValuesBypassThePool()
    {
        var pool = new Sylvan();
        string value = new('x', 40);

        string a = pool.GetString(value.AsSpan());
        string b = pool.GetString(value.AsSpan());

        Assert.Equal(value, a);
        Assert.NotSame(a, b);
    }

    [Theory]
    [InlineData(0, 0, 0x01)]
    [InlineData(7, 0, 0x80)]
    [InlineData(8, 1, 0x01)]
    [InlineData(9, 1, 0x02)]
    [InlineData(15, 1, 0x80)]
    [InlineData(16, 2, 0x01)]
    public void NullBitmapLocationMatchesPhysicalFieldNumber(
        int physicalField, int expectedByteIndex, byte expectedMask)
    {
        DbosTupleDesc.ComputeNullTest(
            physicalField,
            out int byteIndex,
            out byte bitMask);

        Assert.Equal(expectedByteIndex, byteIndex);
        Assert.Equal(expectedMask, bitMask);
    }
}

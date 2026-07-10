using JustyBase.NetezzaDriver.Utility;
using Xunit;

namespace JustyBase.NetezzaDriver.Tests.Utility;

[Trait("Category", "Unit")]
public class PGUtilTests
{
    [Fact]
    public void ReadInt32_ReadsCorrectly()
    {
        // Arrange
        var expected = 123456789;
        var stream = new MemoryStream();
        PGUtil.WriteInt32(stream, expected);
        stream.Position = 0;

        // Act
        var result = PGUtil.ReadInt32(stream);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ReadInt16_ReadsCorrectly()
    {
        // Arrange
        short expected = 12345;
        var stream = new MemoryStream();
        PGUtil.WriteInt16(stream, expected);
        stream.Position = 0;

        // Act
        var result = PGUtil.ReadInt16(stream);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WriteInt32_WritesCorrectly()
    {
        // Arrange
        var value = 987654321;
        var stream = new MemoryStream();

        // Act
        PGUtil.WriteInt32(stream, value);
        stream.Position = 0;
        var result = PGUtil.ReadInt32(stream);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public void WriteInt16_WritesCorrectly()
    {
        // Arrange
        short value = 5432;
        var stream = new MemoryStream();

        // Act
        PGUtil.WriteInt16(stream, value);
        stream.Position = 0;
        var result = PGUtil.ReadInt16(stream);

        // Assert
        Assert.Equal(value, result);
    }

    [Fact]
    public void Skip4Bytes_SkipsCorrectly()
    {
        // Arrange
        var stream = new MemoryStream();
        stream.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 8);
        stream.Position = 0;

        // Act
        PGUtil.Skip4Bytes(stream);
        var remainingByte = stream.ReadByte();

        // Assert
        Assert.Equal(5, remainingByte);
    }
    
    [Fact]
    public void Skip4Bytes_CustomCount_SkipsCorrectly()
    {
        // Arrange
        var stream = new MemoryStream();
        stream.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 8);
        stream.Position = 0;

        // Act
        PGUtil.Skip4Bytes(stream, 2);
        var remainingByte = stream.ReadByte();

        // Assert
        Assert.Equal(3, remainingByte);
    }
}

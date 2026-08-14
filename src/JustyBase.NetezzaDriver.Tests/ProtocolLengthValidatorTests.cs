namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class ProtocolLengthValidatorTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_RejectsNegativeLengths(int length)
    {
        var exception = Assert.Throws<NetezzaException>(() =>
            ProtocolLengthValidator.Validate(length, "rowPayload"));

        Assert.Contains("negative", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rowPayload", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AllowsZeroWhenTheProtocolFieldAllowsEmptyPayload()
    {
        Assert.Equal(0, ProtocolLengthValidator.Validate(0, "noticePayload"));
    }

    [Fact]
    public void Validate_RejectsZeroForRequiredPayload()
    {
        var exception = Assert.Throws<NetezzaException>(() =>
            ProtocolLengthValidator.Validate(0, "dbosPayload", allowZero: false));

        Assert.Contains("zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllowsTheConfiguredMaximum()
    {
        Assert.Equal(
            ProtocolLengthValidator.MaxPayloadLength,
            ProtocolLengthValidator.Validate(
                ProtocolLengthValidator.MaxPayloadLength,
                "messagePayload"));
    }

    [Theory]
    [InlineData(10_000_001)]
    [InlineData(int.MaxValue)]
    public void Validate_RejectsLengthsAboveTheConfiguredMaximum(int length)
    {
        var exception = Assert.Throws<NetezzaException>(() =>
            ProtocolLengthValidator.Validate(length, "messagePayload"));

        Assert.Contains("maximum supported protocol payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsZeroLengthDirectoryHeaderBeforeSubtractingOne()
    {
        var exception = Assert.Throws<NetezzaException>(() =>
            ProtocolLengthValidator.Validate(0, "logDirectoryLength", allowZero: false));

        Assert.Contains("logDirectoryLength", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(10_000_001)]
    [InlineData(int.MaxValue)]
    public void ValidateAfterOverhead_RejectsInvalidFrameBeforeSubtraction(int frameLength)
    {
        var exception = Assert.Throws<NetezzaException>(() =>
            ProtocolLengthValidator.ValidateAfterOverhead(
                frameLength,
                overhead: 1,
                frameField: "logDirectoryLength",
                payloadField: "logDirectoryPayloadLength"));

        Assert.Contains("logDirectory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAfterOverhead_AllowsZeroDerivedPayloadWhenPermitted()
    {
        Assert.Equal(
            0,
            ProtocolLengthValidator.ValidateAfterOverhead(
                frameLength: 1,
                overhead: 1,
                frameField: "frameLength",
                payloadField: "payloadLength"));
    }

    [Fact]
    public void ValidateAfterOverhead_RejectsZeroDerivedPayloadWhenRequired()
    {
        var exception = Assert.Throws<NetezzaException>(() =>
            ProtocolLengthValidator.ValidateAfterOverhead(
                frameLength: 1,
                overhead: 1,
                frameField: "frameLength",
                payloadField: "payloadLength",
                payloadAllowZero: false));

        Assert.Contains("zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateAfterOverhead_RejectsFrameShorterThanItsOverhead()
    {
        var exception = Assert.Throws<NetezzaException>(() =>
            ProtocolLengthValidator.ValidateAfterOverhead(
                frameLength: 2,
                overhead: 3,
                frameField: "frameLength",
                payloadField: "payloadLength"));

        Assert.Contains("overhead", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("payloadLength", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAfterOverhead_RejectsNegativeOverheadAsProgrammingError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProtocolLengthValidator.ValidateAfterOverhead(
                frameLength: 10,
                overhead: -1,
                frameField: "frameLength",
                payloadField: "payloadLength"));
    }
}

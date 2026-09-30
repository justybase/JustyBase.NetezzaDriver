using JustyBase.NetezzaDriver;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class RowDecodeOptimizationTests
{
    [Fact]
    public void FillVariableFieldOffsetsMatchesPaddedFieldWalk()
    {
        byte[] row =
        [
            0, 0, 0,       // fixed field area
            4, 0, 65, 66,  // length includes prefix; even-sized
            5, 0, 67, 68, 69, 0, // odd-sized field padded to even
            2, 0           // empty variable field
        ];
        int[] offsets = new int[3];

        NzConnection.FillVariableFieldOffsets(row, fixedOffset: 3, offsets);

        Assert.Equal(new[] { 3, 7, 13 }, offsets);
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(0, 4, 3)]
    public void FillVariableFieldOffsetsRejectsInvalidOrTruncatedFields(
        int fixedOffset, int declaredLength, int availableBytes)
    {
        byte[] row = new byte[fixedOffset + availableBytes];
        if (availableBytes >= 2)
            BitConverter.GetBytes((short)declaredLength).CopyTo(row, fixedOffset);

        Assert.Throws<InvalidDataException>(() =>
            NzConnection.FillVariableFieldOffsets(row, fixedOffset, new int[1]));
    }

    [Fact]
    public void RowValueResetClearsPreviouslyStoredReference()
    {
        var row = new RowValue
        {
            typeCode = TypeCodeEx.String,
            stringValue = new string('x', 2048)
        };

        row.ResetForReuse();

        Assert.Null(row.stringValue);
        Assert.Equal(TypeCodeEx.Empty, row.typeCode);
    }
}

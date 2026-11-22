using System.Data;
using System.Globalization;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public sealed class ParameterSurfaceTests
{
    [Fact]
    public void Properties_ExposeAdoNetSurfaceAndRejectUnsupportedDirections()
    {
        var parameter = new NzParameter
        {
            ParameterName = "@name",
            DbType = DbType.String,
            Direction = ParameterDirection.Input,
            Size = 42,
            IsNullable = true,
            Precision = 18,
            Scale = 2,
            SourceColumn = "SOURCE",
            SourceColumnNullMapping = true,
            SourceVersion = DataRowVersion.Original,
        };

        Assert.Equal("@name", parameter.ParameterName);
        Assert.Equal("name", parameter.ResolvedName);
        Assert.Equal("name", parameter.GetResolvedNameSpan().ToString());
        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal(ParameterDirection.Input, parameter.Direction);
        Assert.Equal(42, parameter.Size);
        Assert.True(parameter.IsNullable);
        Assert.Equal(18, parameter.Precision);
        Assert.Equal(2, parameter.Scale);
        Assert.Equal("SOURCE", parameter.SourceColumn);
        Assert.True(parameter.SourceColumnNullMapping);
        Assert.Equal(DataRowVersion.Original, parameter.SourceVersion);

        parameter.ResetDbType();
        Assert.Equal(DbType.Object, parameter.DbType);

        Assert.Throws<NotSupportedException>(() =>
            parameter.Direction = ParameterDirection.Output);
    }

    [Fact]
    public void EmptyAndPositionalNames_AreSupported()
    {
        var parameter = new NzParameter();
        Assert.Equal(string.Empty, parameter.ParameterName);
        Assert.Null(parameter.ResolvedName);
        Assert.True(parameter.GetResolvedNameSpan().IsEmpty);

        parameter.ParameterName = ":p";
        Assert.Equal("p", parameter.ResolvedName);
        parameter.IsPositional = true;
        Assert.True(parameter.IsPositional);
    }

    [Theory]
    [InlineData(null, DbType.Object, "NULL")]
    [InlineData(true, DbType.Boolean, "TRUE")]
    [InlineData(false, DbType.Boolean, "FALSE")]
    [InlineData(42, DbType.Int32, "42")]
    [InlineData(-42L, DbType.Int64, "-42")]
    [InlineData((short)-7, DbType.Int16, "-7")]
    [InlineData((byte)7, DbType.Byte, "7")]
    [InlineData((sbyte)-7, DbType.SByte, "-7")]
    [InlineData((ushort)7, DbType.UInt16, "7")]
    [InlineData(7u, DbType.UInt32, "7")]
    [InlineData(7ul, DbType.UInt64, "7")]
    [InlineData(1.5, DbType.Double, "1.5")]
    [InlineData(1.5f, DbType.Single, "1.5")]
    [InlineData("O'Brien", DbType.String, "'O''Brien'")]
    [InlineData('x', DbType.StringFixedLength, "'x'")]
    [InlineData(' ', DbType.StringFixedLength, "' '")]
    public void ValueMapping_ProducesDbTypeAndSqlLiteral(object? value, DbType expectedType, string expectedLiteral)
    {
        var parameter = new NzParameter { Value = value };

        if (value is not null)
        {
            Assert.Equal(expectedType, parameter.DbType);
            Assert.Equal(expectedType, NzParameter.ValueToDbType(value));
        }

        Assert.Equal(expectedLiteral, parameter.ToSqlLiteral());
        Assert.Equal(expectedLiteral, NzParameter.ValueToSqlLiteral(value));
    }

    [Fact]
    public void TemporalAndBinaryValues_UseNetezzaLiterals()
    {
        var timestamp = new DateTime(2024, 2, 29, 12, 34, 56, DateTimeKind.Unspecified).AddTicks(1_234_560);
        Assert.Equal("'2024-02-29 12:34:56.123456'", NzParameter.ValueToSqlLiteral(timestamp));
        Assert.Equal("'2024-02-29'", NzParameter.ValueToSqlLiteral(new DateOnly(2024, 2, 29)));
        Assert.Equal("'12:34:56'", NzParameter.ValueToSqlLiteral(new TimeOnly(12, 34, 56)));
        Assert.Equal("'12:34:56'", NzParameter.ValueToSqlLiteral(new TimeSpan(12, 34, 56)));
        Assert.Equal("x'00ff10'", NzParameter.ValueToSqlLiteral(new byte[] { 0x00, 0xFF, 0x10 }));
        Assert.Equal(
            "'00000000-0000-0000-0000-000000000001'",
            NzParameter.ValueToSqlLiteral(new Guid("00000000-0000-0000-0000-000000000001")));
        Assert.Equal("3.1400", NzParameter.ValueToSqlLiteral(3.1400m));
        Assert.Equal("'custom'", NzParameter.ValueToSqlLiteral(new CustomValue()));
    }

    [Fact]
    public void QuoteCharacter_IsEscaped()
    {
        Assert.Equal("''''", NzParameter.ValueToSqlLiteral('\''));
        Assert.Equal("'a''b''c'", NzParameter.FormatStringLiteral("a'b'c"));
    }

    private sealed class CustomValue
    {
        public override string ToString() => "custom";
    }
}

using System.Data.Common;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class ColumnMetaTests : IDisposable
{
    private readonly NzConnection _conn;

    public ColumnMetaTests()
    {
        _conn = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void GetProviderType_ReturnsOID()
    {
        using var cmd = _conn.CreateCommand("SELECT 1::INTEGER");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var nzReader = (NzDataReader)reader;
        var providerType = nzReader.GetProviderType(0);
        Assert.Equal(23, providerType); // INT4 OID
    }

    [Fact]
    public void GetProviderType_Varchar()
    {
        using var cmd = _conn.CreateCommand("SELECT 'abc'::VARCHAR(10)");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var nzReader = (NzDataReader)reader;
        var providerType = nzReader.GetProviderType(0);
        Assert.Equal(1043, providerType); // VARCHAR OID
    }

    [Fact]
    public void GetDeclaredTypeName_Varchar()
    {
        using var cmd = _conn.CreateCommand("SELECT 'abc'::VARCHAR(32)");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var nzReader = (NzDataReader)reader;
        var name = nzReader.GetDeclaredTypeName(0);
        Assert.Equal("VARCHAR(32)", name);
    }

    [Fact]
    public void GetDeclaredTypeName_Numeric()
    {
        using var cmd = _conn.CreateCommand("SELECT 123.45::NUMERIC(10,2)");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var nzReader = (NzDataReader)reader;
        var name = nzReader.GetDeclaredTypeName(0);
        Assert.StartsWith("NUMERIC", name ?? "");
    }

    [Fact]
    public void GetDeclaredTypeName_Date()
    {
        using var cmd = _conn.CreateCommand("SELECT CURRENT_DATE");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var nzReader = (NzDataReader)reader;
        var name = nzReader.GetDeclaredTypeName(0);
        Assert.Equal("DATE", name);
    }

    [Fact]
    public void GetColumnSchema_ReturnsColumns()
    {
        using var cmd = _conn.CreateCommand("SELECT 1::INTEGER AS id, 'abc'::VARCHAR(10) AS name");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var schema = ((NzDataReader)reader).GetColumnSchema();
        Assert.NotNull(schema);
        Assert.Equal(2, schema.Count);

        var col0 = schema[0] as NzDbColumn;
        Assert.NotNull(col0);
        Assert.Equal("ID", col0.ColumnName);
        Assert.Equal(23, col0.ProviderType);
        Assert.Equal(typeof(int), col0.DataType);

        var col1 = schema[1] as NzDbColumn;
        Assert.NotNull(col1);
        Assert.Equal("NAME", col1.ColumnName);
        Assert.Equal(1043, col1.ProviderType);
        Assert.Equal(typeof(string), col1.DataType);
        Assert.NotNull(col1.DeclaredTypeName);
        Assert.Contains("VARCHAR", col1.DeclaredTypeName);
    }

    [Fact]
    public void GetColumnSchema_NumericPrecision()
    {
        using var cmd = _conn.CreateCommand("SELECT 3.14::NUMERIC(10,4) AS val");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var schema = ((NzDataReader)reader).GetColumnSchema();
        var col = schema[0] as NzDbColumn;
        Assert.NotNull(col);
        Assert.Equal(typeof(decimal), col.DataType);
    }

    [Fact]
    public void GetSchemaTable_StillWorks()
    {
        using var cmd = _conn.CreateCommand("SELECT 1::INTEGER AS id");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var schemaTable = reader.GetSchemaTable();
        Assert.NotNull(schemaTable);
        Assert.Equal(1, schemaTable.Rows.Count);
        Assert.Equal("ID", schemaTable.Rows[0]["ColumnName"]);
    }
}

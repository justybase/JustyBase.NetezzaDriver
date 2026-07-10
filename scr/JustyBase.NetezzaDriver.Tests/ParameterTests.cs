using System.Data;
using System.Data.Common;

namespace JustyBase.NetezzaDriver.Tests;

[Trait("Category", "Unit")]
public class ParameterUnitTests
{
    [Fact]
    public void NzParameter_DefaultValues()
    {
        var p = new NzParameter();
        Assert.Equal(string.Empty, p.ParameterName);
        Assert.Equal(DbType.Object, p.DbType);
        Assert.Equal(ParameterDirection.Input, p.Direction);
        Assert.Equal(0, p.Size);
    }

    [Fact]
    public void NzParameter_NamedConstructor()
    {
        var p = new NzParameter(":name", 42);
        Assert.Equal(":name", p.ParameterName);
        Assert.Equal(42, p.Value);
        Assert.Equal(DbType.Int32, p.DbType);
    }

    [Fact]
    public void NzParameter_ValueToSqlLiteral_String()
    {
        Assert.Equal("'hello'", NzParameter.ValueToSqlLiteral("hello"));
        Assert.Equal("'it''s'", NzParameter.ValueToSqlLiteral("it's"));
        Assert.Equal("NULL", NzParameter.ValueToSqlLiteral(null));
        Assert.Equal("NULL", NzParameter.ValueToSqlLiteral(DBNull.Value));
    }

    [Fact]
    public void NzParameter_ValueToSqlLiteral_Numbers()
    {
        Assert.Equal("42", NzParameter.ValueToSqlLiteral(42));
        Assert.Equal("-1", NzParameter.ValueToSqlLiteral(-1));
        Assert.Equal("3.14", NzParameter.ValueToSqlLiteral(3.14m));
        Assert.Equal("3.14", NzParameter.ValueToSqlLiteral(3.14f));
    }

    [Fact]
    public void NzParameter_ValueToSqlLiteral_Bool()
    {
        Assert.Equal("TRUE", NzParameter.ValueToSqlLiteral(true));
        Assert.Equal("FALSE", NzParameter.ValueToSqlLiteral(false));
    }

    [Fact]
    public void NzParameter_ValueToSqlLiteral_DateTime()
    {
        var dt = new DateTime(2024, 12, 25, 10, 30, 0);
        Assert.Equal("'2024-12-25 10:30:00.000000'", NzParameter.ValueToSqlLiteral(dt));
    }

    [Fact]
    public void NzParameter_ValueToSqlLiteral_DateOnly()
    {
        var d = new DateOnly(2024, 12, 25);
        Assert.Equal("'2024-12-25'", NzParameter.ValueToSqlLiteral(d));
    }

    [Fact]
    public void NzParameter_ValueToSqlLiteral_ByteArray()
    {
        byte[] bytes = [0x41, 0x42, 0x43];
        Assert.Equal("x'414243'", NzParameter.ValueToSqlLiteral(bytes));
    }

    [Fact]
    public void NzParameterCollection_AddAndRetrieve()
    {
        var coll = new NzParameterCollection();
        var p1 = new NzParameter(":name", "Alice");
        var p2 = new NzParameter(":age", 30);

        Assert.Equal(0, coll.Add(p1));
        Assert.Equal(1, coll.Add(p2));
        Assert.Equal(2, coll.Count);
        Assert.Same(p1, coll[0]);
        Assert.Same(p2, coll[1]);
    }

    [Fact]
    public void NzParameterCollection_AddWithValue()
    {
        var coll = new NzParameterCollection();
        var p = coll.AddWithValue(":name", "test");
        Assert.NotNull(p);
        Assert.Equal(":name", p.ParameterName);
        Assert.Equal("test", p.Value);
    }

    [Fact]
    public void NzParameterCollection_NamedLookup()
    {
        var coll = new NzParameterCollection();
        coll.AddWithValue(":name", "Alice");
        coll.AddWithValue(":age", 30);

        Assert.Equal(0, coll.IndexOf(":name"));
        Assert.Equal(1, coll.IndexOf(":age"));
        Assert.Equal(0, coll.IndexOf("name"));
        Assert.Equal(0, coll.IndexOf("@name"));
    }

    [Fact]
    public void NzParameterCollection_Contains()
    {
        var coll = new NzParameterCollection();
        var p = coll.AddWithValue(":x", 1);
        Assert.True(coll.Contains(p));
        Assert.True(coll.Contains(":x"));
        Assert.False(coll.Contains(":y"));
    }

    [Fact]
    public void NzParameterCollection_Remove()
    {
        var coll = new NzParameterCollection();
        coll.AddWithValue(":a", 1);
        var p2 = coll.AddWithValue(":b", 2);
        coll.AddWithValue(":c", 3);

        coll.Remove(p2);
        Assert.Equal(2, coll.Count);
        Assert.Equal(1, coll.IndexOf(":c"));
    }

    [Fact]
    public void NzParameterCollection_RemoveAt()
    {
        var coll = new NzParameterCollection();
        coll.AddWithValue(":a", 1);
        coll.AddWithValue(":b", 2);

        coll.RemoveAt(0);
        Assert.Equal(1, coll.Count);
        Assert.Equal(":b", coll[0].ParameterName);
    }

    [Fact]
    public void NzParameterCollection_Clear()
    {
        var coll = new NzParameterCollection();
        coll.AddWithValue(":a", 1);
        coll.AddWithValue(":b", 2);
        coll.Clear();
        Assert.Equal(0, coll.Count);
        Assert.Equal(-1, coll.IndexOf(":a"));
    }

    [Fact]
    public void NzParameterCollection_GetEnumerator()
    {
        var coll = new NzParameterCollection();
        coll.AddWithValue(":a", 1);
        coll.AddWithValue(":b", 2);

        int count = 0;
        foreach (NzParameter p in coll)
            count++;
        Assert.Equal(2, count);
    }

    [Fact]
    public void NzParameter_ResolvedName()
    {
        var p1 = new NzParameter(":name", "x");
        Assert.Equal("name", p1.ResolvedName);

        var p2 = new NzParameter("@name", "x");
        Assert.Equal("name", p2.ResolvedName);

        var p3 = new NzParameter("name", "x");
        Assert.Equal("name", p3.ResolvedName);
    }

    [Fact]
    public void NzParameter_DirectionInputOnly()
    {
        var p = new NzParameter();
        Assert.Equal(ParameterDirection.Input, p.Direction);
        Assert.Throws<NotSupportedException>(() => p.Direction = ParameterDirection.Output);
    }

    [Fact]
    public void SubstituteParameters_AllowsNamedParametersBeforeCastSyntax()
    {
        var parameters = new NzParameterCollection();
        parameters.AddWithValue(":val", 42);

        var sql = NzParameterHelper.SubstituteParameters("SELECT :val::INTEGER", parameters);

        Assert.Equal("SELECT 42::INTEGER", sql);
    }

    [Fact]
    public void SubstituteParameters_ThrowsWhenNamedAndPositionalAreMixed()
    {
        var parameters = new NzParameterCollection();
        parameters.AddWithValue(":val", 42);
        parameters.Add(new NzParameter { Value = 7, IsPositional = true });

        var ex = Assert.Throws<InvalidOperationException>(() => NzParameterHelper.SubstituteParameters("SELECT :val, ?", parameters));

        Assert.Contains("cannot be mixed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubstituteParameters_ThrowsWhenNamedParameterIsMissing()
    {
        var parameters = new NzParameterCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => NzParameterHelper.SubstituteParameters("SELECT :missing", parameters));

        Assert.Contains(":missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubstituteParameters_ThrowsWhenNamedParameterIsUnused()
    {
        var parameters = new NzParameterCollection();
        parameters.AddWithValue(":unused", 42);

        var ex = Assert.Throws<InvalidOperationException>(() => NzParameterHelper.SubstituteParameters("SELECT 1", parameters));

        Assert.Contains(":unused", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubstituteParameters_ThrowsWhenPositionalParameterIsMissing()
    {
        var parameters = new NzParameterCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => NzParameterHelper.SubstituteParameters("SELECT ?", parameters));

        Assert.Contains("Missing value for SQL parameter '?'", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubstituteParameters_ThrowsWhenPositionalParameterIsUnused()
    {
        var parameters = new NzParameterCollection();
        parameters.Add(new NzParameter { Value = 42, IsPositional = true });

        var ex = Assert.Throws<InvalidOperationException>(() => NzParameterHelper.SubstituteParameters("SELECT 1", parameters));

        Assert.Contains("More positional parameter values", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "Integration")]
public class ParameterIntegrationTests : IDisposable
{
    private readonly NzConnection _conn;

    public ParameterIntegrationTests()
    {
        _conn = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void NamedParameter_Select()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::INTEGER");
        cmd.Parameters.AddWithValue(":val", 42);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(42, reader.GetInt32(0));
    }

    [Fact]
    public void NamedParameter_String()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::VARCHAR(20)");
        cmd.Parameters.AddWithValue(":val", "Hello");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Hello", reader.GetString(0));
    }

    [Fact]
    public void NamedParameter_Decimal()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::NUMERIC(10,4)");
        cmd.Parameters.AddWithValue(":val", 3.1415m);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(3.1415m, reader.GetDecimal(0));
    }

    [Fact]
    public void NamedParameter_Bool()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::BOOLEAN");
        cmd.Parameters.AddWithValue(":val", true);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.GetBoolean(0));
    }

    [Fact]
    public void NamedParameter_Date()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::DATE");
        cmd.Parameters.AddWithValue(":val", new DateOnly(2024, 12, 25));
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(new DateTime(2024, 12, 25), reader.GetDateTime(0));
    }

    [Fact]
    public void NamedParameter_Null()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::INTEGER");
        cmd.Parameters.AddWithValue(":val", null);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
    }

    [Fact]
    public void MultipleNamedParameters()
    {
        using var cmd = _conn.CreateCommand("SELECT :a::INTEGER, :b::VARCHAR(20), :c::BOOLEAN");
        cmd.Parameters.AddWithValue(":a", 1);
        cmd.Parameters.AddWithValue(":b", "test");
        cmd.Parameters.AddWithValue(":c", false);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("test", reader.GetString(1));
        Assert.False(reader.GetBoolean(2));
    }

    [Fact]
    public void PositionalParameter_Select()
    {
        using var cmd = _conn.CreateCommand("SELECT ?::INTEGER");
        var p = new NzParameter { Value = 99 };
        p.IsPositional = true;
        cmd.Parameters.Add(p);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(99, reader.GetInt32(0));
    }

    [Fact]
    public void Parameter_DbParameterInterface_Add()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::INTEGER");
        var p = cmd.CreateParameter();
        p.ParameterName = ":val";
        p.Value = 7;
        cmd.Parameters.Add(p);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(7, reader.GetInt32(0));
    }

    [Fact]
    public void InsertAndSelect_RoundTrip()
    {
        using var cmd = _conn.CreateCommand(
            "CREATE TEMP TABLE param_test (id INTEGER, name VARCHAR(50), amount NUMERIC(10,2))");
        cmd.ExecuteNonQuery();

        using var insertCmd = _conn.CreateCommand(
            "INSERT INTO param_test VALUES (:id, :name, :amount)");
        insertCmd.Parameters.AddWithValue(":id", 1);
        insertCmd.Parameters.AddWithValue(":name", "Alice");
        insertCmd.Parameters.AddWithValue(":amount", 123.45m);
        var affected = insertCmd.ExecuteNonQuery();
        Assert.Equal(1, affected);

        using var selectCmd = _conn.CreateCommand("SELECT id, name, amount FROM param_test WHERE id = :id");
        selectCmd.Parameters.AddWithValue(":id", 1);
        using var reader = selectCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Alice", reader.GetString(1));
        Assert.Equal(123.45m, reader.GetDecimal(2));
    }

    [Fact]
    public void Parameters_AreNotPersistedAcrossExecutions()
    {
        using var cmd = _conn.CreateCommand("SELECT :val::INTEGER");
        cmd.Parameters.AddWithValue(":val", 10);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(10, reader.GetInt32(0));
    }
}

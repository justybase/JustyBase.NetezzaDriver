using System.Text;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]

public class GetSchemaTableTests
{
    [Fact]
    public void GetSchemaTable_ReturnsCorrectColumnSchema()
    {

        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var cmd = connection.CreateCommand();

        // Test with multiple columns of different types
        cmd.CommandText = @"
            SELECT 
                ENGLISHDAYNAMEOFWEEK, CAST(42 AS INTEGER) AS INT_COL,
                CAST('2024-01-01' AS DATE) AS DATE_COL,
                CAST(123.45 AS NUMERIC(10,2)) AS NUMERIC_COL,
                'text123' AS text_col3 FROM JUST_DATA..DIMDATE  D 
                ORDER BY D.DATEKEY
                LIMIT 2";

        using var reader = cmd.ExecuteReader();        
        var schemaTable = reader.GetSchemaTable();

        Assert.NotNull(schemaTable);
        Assert.Equal(5, schemaTable!.Rows.Count);

        // Verify column types
        Assert.Equal(typeof(string), schemaTable.Columns["ColumnName"].DataType);
        Assert.Equal(typeof(int), schemaTable.Columns["ColumnOrdinal"].DataType);
        Assert.Equal(typeof(Int16), schemaTable.Columns["ColumnSize"].DataType);
        Assert.Equal(typeof(int), schemaTable.Columns["NumericPrecision"].DataType);
        Assert.Equal(typeof(int), schemaTable.Columns["NumericScale"].DataType);
        Assert.Equal(typeof(Type), schemaTable.Columns["DataType"].DataType);
        Assert.Equal(typeof(int), schemaTable.Columns["ProviderType"].DataType);
        Assert.Equal(typeof(bool), schemaTable.Columns["AllowDBNull"].DataType);

        // Verify numeric column metadata
        var numericRow = schemaTable.Rows[3];
        Assert.Equal("NUMERIC_COL", numericRow["ColumnName"]);
        Assert.Equal(4, numericRow["ColumnOrdinal"]);
        Assert.Equal<int>(10, (int)numericRow["NumericPrecision"]);
        Assert.Equal<int>(2, (int)numericRow["NumericScale"]);
        Assert.Equal<Int16>(19, (Int16)numericRow["ColumnSize"]);
    }

    [Fact]
    public void GetSchemaTable_WithNotNullColumn()
    {
        string sql = "DROP TABLE TEST_NOT_NULL IF EXISTS;\r\nCREATE TABLE TEST_NOT_NULL \r\n(\r\nID INT NOT NULL\r\n) \r\nDISTRIBUTE ON RANDOM;\r\n\r\nINSERT INTO TEST_NOT_NULL SELECT 15;";

        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var cmd = connection.CreateCommand();

        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT * FROM TEST_NOT_NULL";
        using var reader = cmd.ExecuteReader();
        var schemaTable = reader.GetSchemaTable();

        var row = schemaTable.Rows[0];
        Assert.Equal<bool>(false, (bool)row["AllowDBNull"]);
    }

    [Fact]
    public void GetSchemaTable_WithMixedNullability()
    {
        string sql = "DROP TABLE TEST_NOT_NULL IF EXISTS;\r\nCREATE TABLE TEST_NOT_NULL \r\n(\r\nID INT NOT NULL\r\n,ID2 INT\r\n) \r\nDISTRIBUTE ON RANDOM;\r\n\r\nINSERT INTO TEST_NOT_NULL SELECT 15;";

        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var cmd = connection.CreateCommand();

        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT * FROM TEST_NOT_NULL";
        using var reader = cmd.ExecuteReader();
        var schemaTable = reader.GetSchemaTable();

        var row = schemaTable.Rows[0];
        Assert.Equal<bool>(false, (bool)row["AllowDBNull"]);

        row = schemaTable.Rows[1];
        Assert.Equal<bool>(true, (bool)row["AllowDBNull"]);
    }

    [Fact]
    public void GetSchemaTable_TextColumnSizes()
    {
        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var cmd = connection.CreateCommand();

        // Build SELECT statement with multiple VARCHAR columns of different sizes
        var sb = new StringBuilder("SELECT ");
        var expectedSizes = new List<int>();

        for (int size = 1; size <= 300; size++)
        {
            if (size > 1) sb.Append(',');
            sb.AppendFormat("CAST('x' AS VARCHAR({0})) AS col_{0}", size);
            expectedSizes.Add(size);
        }

        cmd.CommandText = sb.ToString();
        using var reader = cmd.ExecuteReader();
        var schemaTable = reader.GetSchemaTable();

        Assert.NotNull(schemaTable);
        Assert.Equal(300, schemaTable!.Rows.Count);

        // Verify each column's size matches what we specified
        for (int i = 0; i < schemaTable.Rows.Count; i++)
        {
            var row = schemaTable.Rows[i];
            var expectedSize = expectedSizes[i];
            var actualSize = (Int16)row["ColumnSize"];
            var columnName = (string)row["ColumnName"];

            Assert.Equal($"COL_{i + 1}", columnName);
            Assert.Equal((Int16)expectedSize, actualSize);
            Assert.Equal(i + 1, (int)row["ColumnOrdinal"]);
            Assert.Equal(typeof(string), row["DataType"]);
            Assert.True((bool)row["AllowDBNull"]);
        }
    }

    [Fact]
    public void GetSchemaTable_EmptyResultSet()
    {
        // Existing test updated with new column checks
        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var cmd = connection.CreateCommand();

        cmd.CommandText = "SELECT numeric_col FROM (SELECT CAST(0 AS NUMERIC(15,5)) AS numeric_col) t WHERE 1=0";
        using var reader = cmd.ExecuteReader();
        var schemaTable = reader.GetSchemaTable();

        Assert.NotNull(schemaTable);
        Assert.Single(schemaTable!.Rows);

        var row = schemaTable.Rows[0];
        Assert.Equal("NUMERIC_COL", row["ColumnName"]);
        Assert.Equal(1, row["ColumnOrdinal"]);
        Assert.Equal<int>(15, (int)row["NumericPrecision"]);
        Assert.Equal<int>(5, (int)row["NumericScale"]);
        Assert.True(row["ColumnSize"] is Int16);
        Assert.True(row["ProviderType"] is int);
        Assert.True(row["AllowDBNull"] is bool);
    }

    [Fact]
    public void GetSchemaTable_VaryingColumnSizes()
    {
        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var cmd = connection.CreateCommand();

        cmd.CommandText = @"
            SELECT 
                CAST('test' AS CHAR(10)) AS FIXED_CHAR,
                CAST('test' AS VARCHAR(100)) AS VAR_CHAR,
                CAST('test' AS TEXT) AS TEXT_COL";
        using var reader = cmd.ExecuteReader();
        var schemaTable = reader.GetSchemaTable();

        Assert.NotNull(schemaTable);
        Assert.Equal(3, schemaTable!.Rows.Count);

        // Check CHAR(10)
        var fixedCharRow = schemaTable.Rows[0];
        Assert.Equal("FIXED_CHAR", fixedCharRow["ColumnName"]);
        Assert.Equal<short>((short)10, (short)fixedCharRow["ColumnSize"]);

        // Check VARCHAR(100)
        var varCharRow = schemaTable.Rows[1];
        Assert.Equal("VAR_CHAR", varCharRow["ColumnName"]);
        Assert.Equal<short>(100, (short)varCharRow["ColumnSize"]);

        // Check TEXT (should have max size or -1)
        var textRow = schemaTable.Rows[2];
        Assert.Equal("TEXT_COL", textRow["ColumnName"]);
        Assert.True((Int16)textRow["ColumnSize"] == -1 || (Int16)textRow["ColumnSize"] > 0);
    }

    [Fact]
    public void NumericPrecisionScaleTest()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(0);
        using var cmd = connection.CreateCommand();

        string[] froms = ["", "FROM JUST_DATA..DIMDATE LIMIT 1"];

        foreach (var from in froms)
        {
            for (int precision = 1; precision <= 38; precision++)
            {
                for (int scale = 0; scale <= precision && scale <= 28; scale++)
                {
                    cmd.CommandText = $"SELECT 0::NUMERIC({precision},{scale}) AS COL_XYZ {from}";
                    var reader = cmd.ExecuteReader();
                    var st = reader.GetSchemaTable();
                    var numericScale = (int)(st!.Rows[0]["NumericScale"]);
                    var numericPrecision = (int)(st!.Rows[0]["NumericPrecision"]);
                    var columnName = (string)(st!.Rows[0]["ColumnName"]);
                    var columnOrdinal = (int)(st!.Rows[0]["ColumnOrdinal"]);

                    Assert.Equal("COL_XYZ", columnName);
                    Assert.Equal(1, columnOrdinal);

                    //Debug.WriteLine($"{connection.TypeModifierBinary(0)} NUMERIC({precision},{scale})");
                    Assert.Equal(scale, numericScale);
                    Assert.Equal(precision, numericPrecision);
                    do
                    {
                        Console.WriteLine(reader.HasRows);
                        while (reader.Read())
                        {
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var o = reader.GetValue(i);
                            }
                        }
                    } while (reader.NextResult());
                }
            }
        }
    }

    [Fact]
    public void GetSchemaTable_ComputedColumns()
    {
        using var connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        using var cmd = connection.CreateCommand();

        // Create a query with different types of computed columns
        cmd.CommandText = @"
        SELECT 
            CAST(42 AS INTEGER) + 1 AS computed_int,
            SUBSTRING('Hello World', 1, 5) AS computed_string,
            CASE WHEN 1=1 THEN 'Y' ELSE 'N' END AS computed_case,
            COUNT(*) OVER() AS computed_window,
            CAST('2024-01-01' AS DATE) + INTERVAL '1 day' AS computed_date,
            123.45 * 2 AS computed_numeric
        FROM just_data..dimdate";

        using var reader = cmd.ExecuteReader();
        var schemaTable = reader.GetSchemaTable();

        Assert.NotNull(schemaTable);
        Assert.Equal(6, schemaTable!.Rows.Count);

        // Verify computed integer column
        var intRow = schemaTable.Rows[0];
        Assert.Equal("COMPUTED_INT", intRow["ColumnName"]);
        Assert.Equal(typeof(int), intRow["DataType"]);
        Assert.True((bool)intRow["AllowDBNull"]);

        // Verify computed string column
        var stringRow = schemaTable.Rows[1];
        Assert.Equal("COMPUTED_STRING", stringRow["ColumnName"]);
        Assert.Equal(typeof(string), stringRow["DataType"]);
        Assert.Equal<Int16>(5, (Int16)stringRow["ColumnSize"]); // SUBSTRING length

        // Verify computed CASE column
        var caseRow = schemaTable.Rows[2];
        Assert.Equal("COMPUTED_CASE", caseRow["ColumnName"]);
        Assert.Equal(typeof(string), caseRow["DataType"]);
        Assert.Equal<Int16>(1, (Int16)caseRow["ColumnSize"]); // Y or N

        // Verify window function column
        var windowRow = schemaTable.Rows[3];
        Assert.Equal("COMPUTED_WINDOW", windowRow["ColumnName"]);
        Assert.Equal(typeof(long), windowRow["DataType"]); // COUNT(*) returns bigint

        // Verify computed date column
        var dateRow = schemaTable.Rows[4];
        Assert.Equal("COMPUTED_DATE", dateRow["ColumnName"]);
        Assert.Equal(typeof(DateTime), dateRow["DataType"]);

        // Verify computed numeric column
        var numericRow = schemaTable.Rows[5];
        Assert.Equal("COMPUTED_NUMERIC", numericRow["ColumnName"]);
        Assert.Equal(typeof(decimal), numericRow["DataType"]);
        Assert.True(numericRow["NumericPrecision"] is int);
        Assert.True(numericRow["NumericScale"] is int);
    }

}

using System.Data.Odbc;
using Xunit.Sdk;

namespace JustyBase.NetezzaDriver.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]

public class BasicTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    OdbcConnection _odbcConnection;
    NzConnection _nzNewConnection;
    public BasicTests(ITestOutputHelper output)
    {
        _output = output;
        _odbcConnection = new OdbcConnection($"Driver={{NetezzaSQL}};servername={Config.Host};port={Config.Port};database={Config.DbName};username={Config.UserName};password={Config.Password}");
        _odbcConnection.Open();

        _nzNewConnection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName, Config.Port);
        _nzNewConnection.Open();
    }


    [Fact]
    public void aaaa()
    {
        var sql = "SELECT \r\n'2026-01-01 07:00:00'::timestamp as A1, '2025-07-01 07:00:00'::timestamp  AS A2\r\nFROM JUST_DATA..DIMACCOUNT LIMIT 1";
        using var cmd = _nzNewConnection.CreateCommand();
        cmd.CommandText = sql;
        var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            var a1 = (DateTime)rdr.GetValue(0);
            var a2 = (DateTime)rdr.GetValue(1);
        }
    }




    //select
    //    'SELECT * FROM SYSTEM.ADMIN.' || TABLENAME
    //from SYSTEM.._V_TABLE;
    //select 'SELECT * FROM SYSTEM.ADMIN.' || VIEWNAME
    //from SYSTEM.._V_VIEW;
    private const string queryManyTypes = """
        SELECT  
        10::bigint
        , null ::bigint
        , true::Boolean -- ??
        , false::Boolean -- ??
        , null::Boolean
        , 5::Byteint
        , null::Byteint
        , 'a' :: Char
        , null :: Char
        , current_date::Date
        , null::Date
        , 0.5::float
        , null::float
        , 10::integer
        , null::integer
        , 'next should be 02:00:00 time'
        , '02:00:00'::TIME
        , 'abc' ::nchar(10)
        , null ::nchar(10)
        , 1.54::numeric(30, 6)
        , null::numeric(30, 6)
        , 'abc'::Nvarchar(10)
        , null::Nvarchar(10)
        , 1.54::real
        , null::real
        , 5::smallint
        , null::smallint
        --, current_time::time
        , '10:12:13'::TIME
        , null::time
        --, null::Timewithzone
        , DATE_TRUNC('hour',current_timestamp)::Timestamp
        , null:: Timestamp
        , 'abc' ::varchar(10)
        , null ::varchar(10)
        ,* 
        FROM JUST_DATA..FACTPRODUCTINVENTORY 
        order by rowid asc
        LIMIT 1
    """;

    private const string queryManyTypes2 = """
        SELECT  
        10::bigint
        , null ::bigint
        , true::Boolean -- ??
        , false::Boolean -- ??
        , null::Boolean
        , 5::Byteint
        , null::Byteint
        , 'a' :: Char
        , null :: Char
        , current_date::Date
        , null::Date
        , 0.5::float
        , null::float
        , 10::integer
        , null::integer
        , 'next should be 02:00:00 time'
        , '02:00:00'::TIME
        , 'abc' ::nchar(10)
        , null ::nchar(10)
        , 1.54::numeric(30, 6)
        , null::numeric(30, 6)
        , 'abc'::Nvarchar(10)
        , null::Nvarchar(10)
        , 1.54::real
        , null::real
        , 5::smallint
        , null::smallint
        --, current_time::time
        , '10:12:13'::TIME
        , null::time
        --, null::Timewithzone
        , DATE_TRUNC('hour',current_timestamp)::Timestamp
        , null:: Timestamp
        , 'abc' ::varchar(10)
        , null ::varchar(10)
        FROM JUST_DATA.._V_RELATION_COLUMN
        LIMIT 10
    """;

    private readonly string[] _queryListBasic =
    [
        "SELECT '12:00:00'::TIME, '12:00:00'::TIMETZ,'14:13:12.4321+11:15'::TIMETZ",
        "SELECT NOW()",
        "SELECT * FROM JUST_DATA.ADMIN.DIMDATE ORDER BY ROWID LIMIT 1000",
        "SELECT false::BOOLEAN FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 15::BYTEINT FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 'ABC'::VARCHAR(10) FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT '2024-12-12'::DATE FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT '2024-12-12'::TIMESTAMP FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 3.14::NUMERIC(10,4) FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 3.14::NUMERIC(38,8) FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 123456789::NUMERIC(38,0) FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 3.14::REAL FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 3.14::DOUBLE FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 12345678::INTEGER FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT -9223372036854775808::BIGINT FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 9223372036854775807::BIGINT FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT 25000::SMALLINT FROM JUST_DATA.ADMIN.DIMDATE LIMIT 1",
        "SELECT false::BOOLEAN",
        "SELECT 15::BYTEINT",
        "SELECT '2024-12-12'::DATE",
        "SELECT 3.14::NUMERIC(38,8)",
        "SELECT * FROM JUST_DATA.ADMIN.DIMACCOUNT ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM JUST_DATA.ADMIN.DIMDATE ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM JUST_DATA.ADMIN.DIMPRODUCT ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM JUST_DATA.ADMIN.FACTPRODUCTINVENTORY ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM JUST_DATA..NUMERIC_TEST ORDER BY ROWID LIMIT 1000"
    ];

    private readonly string[] _queriesFromSystemTables =
    [
        "SELECT * FROM SYSTEM.ADMIN._T_OBJECT ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._T_DATABASE ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._T_USER ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._T_GROUP ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._T_SCHEMA ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._VT_DUAL ORDER BY ROWID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_DUAL",
        "SELECT * FROM SYSTEM.ADMIN._V_DATABASE ORDER BY OBJID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_USER LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_GROUP LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_SCHEMA LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_TABLE LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_VIEW ORDER BY OBJID LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_INDEX LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_RELATION_COLUMN LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_DATATYPE LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_SEQUENCE LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_FUNCTION LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_OBJECT LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_SYS_DATABASE LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_SYSTEM_INFO LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_CONNECTION LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_ODBC_FEATURE LIMIT 1000",
        "SELECT * FROM SYSTEM.ADMIN._V_DOTNET_FEATURE LIMIT 1000"
    ];

    [Theory]
    [InlineData("SELECT '2 years 5 hours 11 months 41 minutes 15 sec'::interval FROM JUST_DATA..DIMDATE LIMIT 1", "2 years 11 mons 05:41:15 String")]
    [InlineData("SELECT '5 hours 41 minutes  15 sec'::interval FROM JUST_DATA..DIMDATE LIMIT 1", "05:41:15 String")]
    [InlineData("SELECT '05:41:15'::TIME FROM JUST_DATA..DIMDATE LIMIT 1", "05:41:15 TimeSpan")]
    [InlineData("SELECT '2 years 5 hours 11 months 41 minutes 15 sec'::interval", "2 years 11 mons 05:41:15 String")]
    [InlineData("SELECT '5 hours 41 minutes  15 sec'::interval", "05:41:15 String")]
    [InlineData("SELECT '05:41:15'::TIME", "05:41:15 TimeSpan")]

    public void ExpectedIntervalTime(string value1, string expected)
    {
        using var nzCommand = _nzNewConnection.CreateCommand();
        nzCommand.CommandText = value1;
        var rdr = nzCommand.ExecuteReader();
        rdr.Read();
        var val1 = rdr.GetValue(0);
        var result = $"{val1} {val1.GetType().Name}";

        Assert.Equal(expected, result);
    }




    //CREATE TABLE TEST_NUM_TXT AS SELECT '1' AS COL
    //INSERT INTO  TEST_NUM_TXT SELECT 'X';


    [Fact]
    public void SqlQueries_WithExpectedExceptions_ShouldThrowException()
    {
        using var cmd = _nzNewConnection.CreateCommand();
        cmd.CommandText = "SELECT 1/0 FROM TEST_NUM_TXT";

        Assert.ThrowsAny<Exception>(() =>
        {
            var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                // Just iterate through the results if no exception is thrown
            }
        });

        using var cmdCatalog = _nzNewConnection.CreateCommand();
        cmdCatalog.CommandText = "SELECT CURRENT_CATALOG";
        var res = cmdCatalog.ExecuteScalar() as string;
    }

    [Fact]
    public void SqlQueries_WithExpectedExceptions_ShouldThrowException_2()
    {
        using var cmd = _nzNewConnection.CreateCommand();
        cmd.CommandText = "SELECT SUM(X.COL::INT) FROM TEST_NUM_TXT X";

        Assert.ThrowsAny<Exception>(() =>
        {
            var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                // Just iterate through the results if no exception is thrown
            }
        });

        using var cmdCatalog = _nzNewConnection.CreateCommand();
        cmdCatalog.CommandText = "SELECT CURRENT_CATALOG";
        var res = cmdCatalog.ExecuteScalar() as string;
    }


    [Fact]
    public void TestConenctionString()
    {
        string connectionString = $"xyz=123;USERNAME={Config.UserName};PASSWORD={Config.Password};PORT={Config.Port};HOST={Config.Host};DATABASE={Config.DbName};TIMEOUT=5;";
        using var connection = new NzConnection(connectionString);
        connection.Open();
        Assert.True(connection.State == System.Data.ConnectionState.Open, "Connection should be open");
        connection.Close();
    }

    [Fact]
    public void SampleCommandValidateMethod1()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(0);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM JUST_DATA..DIMEMPLOYEE";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            
        }
    }
    [Fact]
    public void SampleCommandValidateMethod2()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(0);
        var cmd = new NzCommand(connection)
        {
            CommandText = "SELECT * FROM JUST_DATA..DIMEMPLOYEE"
        };
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {

        }
    }

    [Fact]
    public void ValidateAccessByIndexOrName()
    {
        using NzConnection connection = new NzConnection(Config.UserName, Config.Password, Config.Host, Config.DbName);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(0);
        using var cmd = connection.CreateCommand();

        string[] cols = ["EMPLOYEEKEY", "PARENTEMPLOYEEKEY", "EMPLOYEENATIONALIDALTERNATEKEY", "FIRSTNAME", "BIRTHDATE", "TITLE", "LOGINID"];
        cmd.CommandText = $"SELECT {string.Join(',', cols)} FROM JUST_DATA..DIMEMPLOYEE";
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            string?[] vals = new string?[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                var o1 = rdr[cols[i]]?.ToString();
                var o2 = rdr[i]?.ToString();
                var o3 = rdr.GetValue(i)?.ToString();
                Assert.Equal(o1, o2);
                Assert.Equal(o1, o3);
                vals[i] = o1;
            }
            object[] vals2 = new object[rdr.FieldCount];
            rdr.GetValues(vals2);
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                Assert.Equal(vals[i], vals2[i]?.ToString());
            }
        }
    }


    [Fact]
    public void ValidateAccessByIndexOrName2()
    {
        string connectionString = $"xyz=123;USERNAME={Config.UserName};PASSWORD={Config.Password};PORT={Config.Port};HOST={Config.Host};DATABASE={Config.DbName};TIMEOUT=5;";
        using var connection = new NzConnection(connectionString);
        connection.Open();
        connection.CommandTimeout = TimeSpan.FromSeconds(0);

        string[] cols = ["EMPLOYEEKEY", "PARENTEMPLOYEEKEY", "EMPLOYEENATIONALIDALTERNATEKEY", "FIRSTNAME", "BIRTHDATE", "TITLE", "LOGINID"];
        using var cmd = connection.CreateCommand($"SELECT {string.Join(',', cols)} FROM JUST_DATA..DIMEMPLOYEE");


        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
        {
            string?[] vals = new string?[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                var o1 = rdr[cols[i]]?.ToString();
                var o2 = rdr[i]?.ToString();
                var o3 = rdr.GetValue(i)?.ToString();
                Assert.Equal(o1, o2);
                Assert.Equal(o1, o3);
                vals[i] = o1;
            }
            object[] vals2 = new object[rdr.FieldCount];
            rdr.GetValues(vals2);
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                Assert.Equal(vals[i], vals2[i]?.ToString());
            }
        }
    }


    [Fact]
    public void CheckDateTimeConsistency()
    {
        string[] queries = ["DROP TABLE NEW_CREATED_TABLE IF EXISTS;\r\nCREATE TABLE NEW_CREATED_TABLE AS (SELECT NOW() AS CREATION_DATE_TIME);SELECT CREATEDATE,NOW()::datetime::varchar(30) FROM _V_TABLE WHERE TABLENAME = 'NEW_CREATED_TABLE'",
        "SELECT CREATEDATE,CREATEDATE::datetime::varchar(30) FROM SYSTEM.ADMIN._V_TABLE_STORAGE_STAT WHERE OBJTYPE = 'TABLE' AND TABLENAME = 'NEW_CREATED_TABLE'",
        "SELECT NOW(), NOW()::datetime::varchar(30)"];

        foreach (var query in queries)
        {
            using var cmd = _nzNewConnection.CreateCommand();
            cmd.CommandText = query;
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var dateTimeValue = (DateTime)rdr.GetValue(0);
                Assert.Equal(dateTimeValue.ToString("yyyy-MM-dd HH:mm:ss")[0..13], ((string)rdr.GetValue(1))[0..13]);
                Assert.Equal(rdr.GetDateTime(0).ToString("yyyy-MM-dd HH:mm:ss")[0..13], ((string)rdr.GetValue(1))[0..13]);
            }
        }
    }


    [Fact(Timeout = 20000)]
    public void OdbcAndNzResultsShouldMatch()
    {
        foreach (var query in _queryListBasic)
        {
            _output.WriteLine($"Query {query}");    
            ValidateTypedQueryResultsByGetValue( query);
        }
    }
    [Fact(Timeout = 20000)]
    public void OdbcAndNzResultsShouldMatchSystem()
    {
        foreach (var query in _queriesFromSystemTables)
        {
            _output.WriteLine($"Query {query}");
            ValidateTypedQueryResultsByGetValue(query);
        }
    }

    private readonly string[] _queriesShouldMatchFast =
    [
        "SELECT * FROM JUST_DATA..DIMDATE ORDER BY DATEKEY LIMIT 1500",
        "SELECT NULL FROM ONE_ROW_TABLE UNION ALL SELECT 'XXXX' FROM ONE_ROW_TABLE",
        "SELECT NULL UNION ALL SELECT 'XXXX'"
    ];

    [Fact]
    public void OdbcAndNzResultsShouldMatchFastGetValue()
    {
        foreach (var query in _queriesShouldMatchFast)
        {
            _output.WriteLine($"Query {query}");
            ValidateTypedQueryResultsByGetValue(query);
        }
    }

    [Fact]
    public void GetString_OnNullValue_ThrowsException()
    {
        // Arrange
        using var cmd = _nzNewConnection.CreateCommand();
        cmd.CommandText = "SELECT NULL::VARCHAR(10) as null_text, 'abc' as non_null_text";
        using var reader = cmd.ExecuteReader();

        // Act & Assert
        Assert.True(reader.Read());

        // Verify first column (null value)
        Assert.True(reader.IsDBNull(0));
        var ex = Assert.Throws<InvalidCastException>(() => reader.GetString(0));
        Assert.Contains("Cannot cast", ex.Message);

        // Verify second column (non-null value)
        Assert.False(reader.IsDBNull(1));
        var value = reader.GetString(1);
        Assert.Equal("abc", value);
    }

    [Theory]
    [InlineData("SELECT NULL::VARCHAR(10), NULL::NVARCHAR(10), NULL::CHAR(10), NULL::NCHAR(10)")]
    [InlineData("SELECT NULL::VARCHAR(10)")]
    [InlineData("SELECT NULL::VARCHAR(10) FROM ONE_ROW_TABLE")]
    public void GetString_OnVariousNullStringTypes_ThrowsException(string query)
    {
        // Arrange
        using var cmd = _nzNewConnection.CreateCommand();
        cmd.CommandText = query;
        using var reader = cmd.ExecuteReader();

        // Act & Assert
        Assert.True(reader.Read());

        for (int i = 0; i < reader.FieldCount; i++)
        {
            Assert.True(reader.IsDBNull(i));
            var ex = Assert.Throws<InvalidCastException>(() => reader.GetString(i));
            Assert.Contains("Cannot cast", ex.Message);
        }
    }


    [Fact]
    public void GetString_OnMixedNullAndNonNullValues_HandlesCorrectly()
    {
        // Arrange
        using var cmd = _nzNewConnection.CreateCommand();
        cmd.CommandText = @"
        SELECT 
            NULL::VARCHAR(10) as c1,
            'abc' as c2,
            NULL::TEXT as c3,
            'def' as c4,
            NULL::NCHAR(10) as c5
    ";
        using var reader = cmd.ExecuteReader();

        // Act & Assert
        Assert.True(reader.Read());

        // Test null columns
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(4));

        // Verify exceptions for null columns
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
        Assert.Throws<InvalidCastException>(() => reader.GetString(2));
        Assert.Throws<InvalidCastException>(() => reader.GetString(4));

        // Test non-null columns
        Assert.False(reader.IsDBNull(1));
        Assert.False(reader.IsDBNull(3));
        Assert.Equal("abc", reader.GetString(1));
        Assert.Equal("def", reader.GetString(3));
    }



    private void ValidateTypedQueryResultsByGetValue(string query)
    {
        //Stopwatch stopwatch = Stopwatch.StartNew();
        using var cmd1 = _odbcConnection.CreateCommand();
        cmd1.CommandText = query;
        using var readerOdbc = cmd1.ExecuteReader();

        using var cmd2 = _nzNewConnection.CreateCommand();
        cmd2.CommandText = query;
        using var readerNz = cmd2.ExecuteReader();

        bool r1 = readerOdbc.Read();
        bool r2 = readerNz.Read();
        int num = 0;
        
        while (r1 && r2)
        {
            num++;
            Assert.True(num < 2000, $"Too many rows returned {query}");
            Assert.Equal(readerOdbc.FieldCount, readerNz.FieldCount);
            for (int i = 0; i < readerOdbc.FieldCount; i++)
            {
                Assert.Equal(readerOdbc.IsDBNull(i), readerNz.IsDBNull(i));
                if (readerOdbc.IsDBNull(i))
                {
                    continue;
                }

                Assert.Equal(readerOdbc.GetFieldType(i), readerNz.GetFieldType(i));

                var odbcObjValue = readerOdbc.GetValue(i);
                var nzObjValue = readerNz.GetValue(i);

                if (odbcObjValue is string strOdbc  && nzObjValue is string strNz)
                {
                    if (strOdbc.Length > 4000)
                    {
                        strOdbc = strOdbc[0..4000];
                    }
                    if (strNz.Length > 4000)
                    {
                        strNz = strNz[0..4000];
                    }
                    Assert.Equal(strOdbc, strNz);
                }
                else if (odbcObjValue is DateTime datetimeOdbc && nzObjValue is DateTime datetimeNz)
                {
                    Assert.Equal(datetimeOdbc, datetimeNz, TimeSpan.FromSeconds(15));
                }
                else
                {
                    Assert.Equal(odbcObjValue, nzObjValue);
                }

                if (readerNz.GetFieldType(i) == typeof(byte))
                {
                    var o1 = readerNz.GetByte(i);
                    Assert.Equal(o1, nzObjValue);
                }
                else if (readerNz.GetFieldType(i) == typeof(Int16))
                {
                    var o1 = readerNz.GetInt16(i);
                    Assert.Equal(o1, nzObjValue);
                }
                else if (readerNz.GetFieldType(i) == typeof(int))
                {
                    var o1 = readerNz.GetInt32(i);
                    Assert.Equal(o1, nzObjValue);
                }
                else if (readerNz.GetFieldType(i) == typeof(long))
                {
                    var o1 = readerNz.GetInt64(i);
                    Assert.Equal(o1, nzObjValue);
                }
                else if (readerNz.GetFieldType(i) == typeof(DateTime))
                {
                    var o1 = readerNz.GetDateTime(i);
                    Assert.Equal(o1, (DateTime)nzObjValue, precision: TimeSpan.FromSeconds(15));
                }
                else if (readerNz.GetFieldType(i) == typeof(decimal))
                {
                    var o1 = readerNz.GetDecimal(i);
                    Assert.Equal(o1, nzObjValue);
                }
                else if (readerNz.GetFieldType(i) == typeof(float))
                {
                    var o1 = readerNz.GetFloat(i);
                    Assert.Equal(o1, nzObjValue);
                }
                else if (readerNz.GetFieldType(i) == typeof(double))
                {
                    var o1 = readerNz.GetDouble(i);
                    Assert.Equal(o1, nzObjValue);
                }
                else if (readerNz.GetFieldType(i) == typeof(string))
                {
                    var o1 = readerNz.GetString(i);
                    Assert.Equal(o1, nzObjValue);
                }
            }

            r1 = readerOdbc.Read();
            r2 = readerNz.Read();
        }
        Assert.Equal(r1,r2);//same number of rows    
    }

    public void Dispose()
    {
        _odbcConnection.Dispose();
        _nzNewConnection.Dispose();
    }

}



//--SELECT * FROM JUST_DATA..NUMERIC_TEST;
//--DROP TABLE NUMERIC_TEST;
//CREATE TABLE NUMERIC_TEST AS 
//(
//    SELECT 
//    null::numeric(38,8) as c1
//    , 3.14::numeric(38,8)  as c2
//    , 123.12::numeric(8,2) as c3
//    , -123.12::numeric(8,2) as c4
//    , 123.1234::numeric(8,4) as c5
//    , -123.1234::numeric(8,4) as c6
//    , 3.14 ::numeric(38,8) as c
//    , 923281625142643375987.43950777::numeric(38,8) AS C8 -- to big for c# numeric
//    , -923281625142643375987.43950777::numeric(38,8) AS C9 -- to big for c# numeric
    
//    ,3.14::numeric(20,4) AS C10
//    , ((0.5 - RANDOM())*1000000000 + random()::numeric(20,4))::numeric(20,4)  AS C11
//    , (0.5 - RANDOM())::numeric(20,8) as x1,  10*x1+ random()::numeric(20,4) AS C12
//    , 100*x1+ random()::numeric(20,4) AS C13
//    , 1000*x1+ random()::numeric(20,4) AS C14
//    , 10000*x1+ random()::numeric(20,4) AS C15
    
//    , 10000*(0.5 - RANDOM())::numeric(10,0) AS C16
//    , 10000*(0.5 - RANDOM())::numeric(10,1) AS C17
//    , 10000*(0.5 - RANDOM())::numeric(10,2) AS C18
//    , 10000*(0.5 - RANDOM())::numeric(10,3) AS C19
//    , 10000*(0.5 - RANDOM())::numeric(10,4) AS C20
//    , 10000*(0.5 - RANDOM())::numeric(10,5) AS C21
//    , 10000*(0.5 - RANDOM())::numeric(10,6) AS C22
//    , 10000*(0.5 - RANDOM())::numeric(10,7) AS C23
//    , 10000*(0.5 - RANDOM())::numeric(10,8) AS C24
//    , 10000*(0.5 - RANDOM())::numeric(10,8) AS C25
    
//    , 10000*(0.5 - RANDOM())::numeric(14,0) AS C26
//    , 10000*(0.5 - RANDOM())::numeric(14,1) AS C27
//    , 10000*(0.5 - RANDOM())::numeric(14,2) AS C28
//    , 10000*(0.5 - RANDOM())::numeric(14,3) AS C29
//    , 10000*(0.5 - RANDOM())::numeric(14,4) AS C30
//    , 10000*(0.5 - RANDOM())::numeric(14,5) AS C31
//    , 10000*(0.5 - RANDOM())::numeric(14,6) AS C32
//    , 10000*(0.5 - RANDOM())::numeric(14,7) AS C33
//    , 10000*(0.5 - RANDOM())::numeric(14,8) AS C34
//    , 10000*(0.5 - RANDOM())::numeric(14,8) AS C35
    
//    , 10000*(0.5 - RANDOM())::numeric(20,0) AS C36
//    , 10000*(0.5 - RANDOM())::numeric(20,1) AS C37
//    , 10000*(0.5 - RANDOM())::numeric(20,2) AS C38
//    , 10000*(0.5 - RANDOM())::numeric(20,3) AS C39
//    , 10000*(0.5 - RANDOM())::numeric(20,4) AS C400
//    , 10000*(0.5 - RANDOM())::numeric(20,5) AS C41
//    , 10000*(0.5 - RANDOM())::numeric(20,6) AS C42
//    , 10000*(0.5 - RANDOM())::numeric(20,7) AS C43
//    , 10000*(0.5 - RANDOM())::numeric(20,8) AS C44
//    , 10000*(0.5 - RANDOM())::numeric(20,8) AS C45
    
        
//    , 10000*(0.5 - RANDOM())::numeric(24,0) AS C46
//    , 10000*(0.5 - RANDOM())::numeric(24,1) AS C47
//    , 10000*(0.5 - RANDOM())::numeric(24,2) AS C48
//    , 10000*(0.5 - RANDOM())::numeric(24,3) AS C49
//    , 10000*(0.5 - RANDOM())::numeric(24,4) AS C40
//    , 10000*(0.5 - RANDOM())::numeric(24,5) AS C51
//    , 10000*(0.5 - RANDOM())::numeric(24,6) AS C52
//    , 10000*(0.5 - RANDOM())::numeric(24,7) AS C53
//    , 10000*(0.5 - RANDOM())::numeric(24,8) AS C54
//    , 10000*(0.5 - RANDOM())::numeric(24,8) AS C55 
    
    
//    , 10000*(0.5 - RANDOM())::numeric(30,0) AS C146
//    , 10000*(0.5 - RANDOM())::numeric(30,1) AS C147
//    , 10000*(0.5 - RANDOM())::numeric(30,2) AS C148
//    , 10000*(0.5 - RANDOM())::numeric(30,3) AS C149
//    , 10000*(0.5 - RANDOM())::numeric(30,4) AS C140
//    , 10000*(0.5 - RANDOM())::numeric(30,5) AS C151
//    , 10000*(0.5 - RANDOM())::numeric(30,6) AS C152
//    , 10000*(0.5 - RANDOM())::numeric(30,7) AS C153
//    , 10000*(0.5 - RANDOM())::numeric(30,8) AS C154
//    , 10000*(0.5 - RANDOM())::numeric(30,8) AS C155
    
        
//    , 10000*(0.5 - RANDOM())::numeric(38,0) AS C246
//    , 10000*(0.5 - RANDOM())::numeric(38,1) AS C247
//    , 10000*(0.5 - RANDOM())::numeric(38,2) AS C248
//    , 10000*(0.5 - RANDOM())::numeric(38,3) AS C249
//    , 10000*(0.5 - RANDOM())::numeric(38,4) AS C240
//    , 10000*(0.5 - RANDOM())::numeric(38,5) AS C251
//    , 10000*(0.5 - RANDOM())::numeric(38,6) AS C252
//    , 10000*(0.5 - RANDOM())::numeric(38,7) AS C253
//    , 10000*(0.5 - RANDOM())::numeric(38,8) AS C254
//    , 10000*(0.5 - RANDOM())::numeric(38,8) AS C255
    
//    ,*
//    FROM JUST_DATA..FACTPRODUCTINVENTORY ORDER BY ROWID
//    LIMIT 10000

//) DISTRIBUTE ON RANDOM;


//CREATE TABLE ONE_ROW_TABLE AS(SELECT 1 AS ID) DISTRIBUTE ON RANDOM;
